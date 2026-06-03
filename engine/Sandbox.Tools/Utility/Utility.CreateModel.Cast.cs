using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Cast.NET;
using Cast.NET.Nodes;

using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace Editor;

public static partial class EditorUtility
{
	const string CastFallbackMaterial = "materials/dev/primary_white.vmat";
	const float CastDegenerateTriangleEpsilon = 0.00000001f;

	internal sealed class CastMeshData
	{
		public string Name { get; init; } = string.Empty;
		public string MaterialName { get; init; } = CastFallbackMaterial;
		public global::Vector3[] Positions { get; init; } = [];
		public CastTriangle[] Faces { get; init; } = [];
		public global::Vector3[] Normals { get; init; } = [];
		public global::Vector2[] TexCoords { get; init; } = [];
		public int SkippedOutOfRangeFaces { get; init; }
		public int SkippedDegenerateFaces { get; init; }
	}

	internal readonly record struct CastTriangle( int A, int B, int C );

	/// <summary>
	/// Create a vmdl file from a static CAST model. V1 imports meshes, positions, normals, UV0 and triangle faces.
	/// </summary>
	public static unsafe Asset CreateModelFromCastFile( Asset castFile, string targetAbsolutePath, CreateModelFromMeshDialog.CollisionMode collisionMode )
	{
		if ( castFile is null )
			return null;

		if ( string.IsNullOrWhiteSpace( targetAbsolutePath ) )
			return null;

		var sourcePath = castFile.AbsolutePath;
		if ( string.IsNullOrWhiteSpace( sourcePath ) || !File.Exists( sourcePath ) )
			return null;

		if ( !g_pToolFramework2.InitEngineTool( "modeldoc_editor" ) )
			return null;

		Cast.NET.Cast cast;
		try
		{
			cast = CastReader.Load( sourcePath );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Failed to load CAST model \"{sourcePath}\": {ex.Message}" );
			return null;
		}

		var meshes = new List<CModelMesh>();
		var collisionTypes = new List<int>();

		try
		{
			foreach ( var model in EnumerateCastModels( cast ) )
			{
				foreach ( var castMesh in model.EnumerateMeshes() )
				{
					if ( !TryCreateModelMeshFromCast( model, castMesh, out var modelMesh ) )
						continue;

					meshes.Add( modelMesh );
					collisionTypes.Add( ToModelDocCollisionType( collisionMode ) );
				}
			}

			if ( meshes.Count == 0 )
			{
				Log.Warning( $"CAST model \"{sourcePath}\" did not contain any valid static meshes." );
				return null;
			}

			var targetDirectory = Path.GetDirectoryName( targetAbsolutePath );
			if ( !string.IsNullOrWhiteSpace( targetDirectory ) )
				Directory.CreateDirectory( targetDirectory );

			var meshesSpan = CollectionsMarshal.AsSpan( meshes );
			var collisionTypesSpan = CollectionsMarshal.AsSpan( collisionTypes );
			fixed ( CModelMesh* pMeshes = meshesSpan )
			fixed ( int* pCollisionTypes = collisionTypesSpan )
			{
				if ( !NativeEngine.ModelDoc.CreateModelFromMeshesWithCollision( targetAbsolutePath, (IntPtr)pMeshes, (IntPtr)pCollisionTypes, meshes.Count ) )
				{
					Log.Warning( $"Failed to create model document \"{targetAbsolutePath}\" from CAST model \"{sourcePath}\"." );
					return null;
				}
			}
		}
		finally
		{
			foreach ( var mesh in meshes )
			{
				if ( mesh.IsValid )
					mesh.DeleteThis();
			}
		}

		var asset = AssetSystem.RegisterFile( targetAbsolutePath );
		if ( asset is null )
			return null;

		asset.Compile( true );
		return asset;
	}

	static IEnumerable<ModelNode> EnumerateCastModels( Cast.NET.Cast cast )
	{
		foreach ( var root in cast.RootNodes )
		{
			if ( root is ModelNode rootModel )
				yield return rootModel;

			foreach ( var model in root.EnumerateChildrenOfType<ModelNode>() )
				yield return model;
		}
	}

	static unsafe bool TryCreateModelMeshFromCast( ModelNode model, MeshNode castMesh, out CModelMesh modelMesh )
	{
		modelMesh = default;

		if ( !TryCreateCastMeshData( model, castMesh, out var meshData ) )
			return false;

		modelMesh = CModelMesh.Create();
		modelMesh.AddVertices( meshData.Positions.Length );
		modelMesh.AddFaceGroup( meshData.MaterialName );

		fixed ( global::Vector3* pPositions = &meshData.Positions[0] )
			modelMesh.SetPositions( (IntPtr)pPositions, meshData.Positions.Length );

		foreach ( var face in meshData.Faces )
		{
			var indices = new[] { face.A, face.B, face.C };
			fixed ( int* pIndices = &indices[0] )
				modelMesh.AddFace( 0, (IntPtr)pIndices, indices.Length );
		}

		fixed ( global::Vector3* pNormals = &meshData.Normals[0] )
			modelMesh.SetNormals( (IntPtr)pNormals, meshData.Normals.Length );

		fixed ( global::Vector2* pUvs = &meshData.TexCoords[0] )
			modelMesh.SetTexCoords( (IntPtr)pUvs, meshData.TexCoords.Length );

		return true;
	}

	internal static bool TryCreateCastMeshData( ModelNode model, MeshNode castMesh, out CastMeshData meshData )
	{
		meshData = null;

		if ( !castMesh.Properties.TryGetValue( "vp", out _ ) )
		{
			Log.Warning( $"Skipping CAST mesh \"{GetCastMeshName( castMesh )}\" because it has no vertex position buffer." );
			return false;
		}

		if ( !castMesh.Properties.TryGetValue( "f", out _ ) )
		{
			Log.Warning( $"Skipping CAST mesh \"{GetCastMeshName( castMesh )}\" because it has no face buffer." );
			return false;
		}

		var sourcePositions = castMesh.VertexPositionBuffer.Values;
		if ( sourcePositions.Count == 0 )
		{
			Log.Warning( $"Skipping CAST mesh \"{GetCastMeshName( castMesh )}\" because it has no vertices." );
			return false;
		}

		var transform = GetCastModelTransform( model );
		var transformedPositions = new NumericsVector3[sourcePositions.Count];
		var positions = new global::Vector3[sourcePositions.Count];

		for ( var i = 0; i < sourcePositions.Count; i++ )
		{
			transformedPositions[i] = TransformCastPosition( sourcePositions[i], transform );
			positions[i] = ToSandboxVector3( transformedPositions[i] );
		}

		var sourceNormals = castMesh.VertexNormalBuffer?.Values;
		var sourceUvs = castMesh.GetUVLayer( 0 )?.Values;

		if ( sourceNormals is null || sourceNormals.Count == 0 )
			Log.Warning( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has no normals; triangle normals will be generated." );
		else if ( sourceNormals.Count < sourcePositions.Count )
			Log.Warning( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has fewer normals than vertices; missing normals will be generated." );

		if ( sourceUvs is null || sourceUvs.Count == 0 )
			Log.Warning( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has no UV0 layer; zero UVs will be used." );
		else if ( sourceUvs.Count < sourcePositions.Count )
			Log.Warning( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has fewer UV0 coordinates than vertices; missing UVs will use zero." );

		var faces = new List<CastTriangle>();
		var faceVertexNormals = new List<global::Vector3>();
		var faceVertexUvs = new List<global::Vector2>();
		var skippedOutOfRangeFaces = 0;
		var skippedDegenerateFaces = 0;

		foreach ( var (a, b, c) in castMesh.EnumerateFaceIndices() )
		{
			if ( !IsValidCastFaceIndex( a, sourcePositions.Count ) || !IsValidCastFaceIndex( b, sourcePositions.Count ) || !IsValidCastFaceIndex( c, sourcePositions.Count ) )
			{
				skippedOutOfRangeFaces++;
				continue;
			}

			if ( a == b || b == c || a == c )
			{
				skippedDegenerateFaces++;
				continue;
			}

			var faceNormal = NumericsVector3.Cross( transformedPositions[b] - transformedPositions[a], transformedPositions[c] - transformedPositions[a] );
			if ( !TryNormalizeCastVector( faceNormal, out faceNormal ) )
			{
				skippedDegenerateFaces++;
				continue;
			}

			faces.Add( new CastTriangle( a, b, c ) );
			AddFaceVertexData( a, sourceNormals, sourceUvs, transform, faceNormal, faceVertexNormals, faceVertexUvs );
			AddFaceVertexData( b, sourceNormals, sourceUvs, transform, faceNormal, faceVertexNormals, faceVertexUvs );
			AddFaceVertexData( c, sourceNormals, sourceUvs, transform, faceNormal, faceVertexNormals, faceVertexUvs );
		}

		if ( skippedOutOfRangeFaces > 0 )
			Log.Warning( $"CAST mesh \"{GetCastMeshName( castMesh )}\" skipped {skippedOutOfRangeFaces} face(s) with out-of-range indices." );

		if ( skippedDegenerateFaces > 0 )
			Log.Warning( $"CAST mesh \"{GetCastMeshName( castMesh )}\" skipped {skippedDegenerateFaces} degenerate face(s)." );

		if ( faces.Count == 0 )
		{
			Log.Warning( $"Skipping CAST mesh \"{GetCastMeshName( castMesh )}\" because it has no valid triangle faces." );
			return false;
		}

		meshData = new CastMeshData
		{
			Name = GetCastMeshName( castMesh ),
			MaterialName = GetCastMeshMaterial( castMesh ),
			Positions = positions,
			Faces = faces.ToArray(),
			Normals = faceVertexNormals.ToArray(),
			TexCoords = faceVertexUvs.ToArray(),
			SkippedOutOfRangeFaces = skippedOutOfRangeFaces,
			SkippedDegenerateFaces = skippedDegenerateFaces
		};

		return true;
	}

	static void AddFaceVertexData(
		int vertexIndex,
		IReadOnlyList<NumericsVector3> sourceNormals,
		IReadOnlyList<NumericsVector2> sourceUvs,
		CastModelTransform transform,
		NumericsVector3 faceNormal,
		List<global::Vector3> faceVertexNormals,
		List<global::Vector2> faceVertexUvs )
	{
		var normal = faceNormal;
		if ( sourceNormals is not null && vertexIndex < sourceNormals.Count )
		{
			var transformedNormal = NumericsVector3.TransformNormal( sourceNormals[vertexIndex], transform.NormalTransform );
			if ( TryNormalizeCastVector( transformedNormal, out var normalizedNormal ) )
				normal = normalizedNormal;
		}

		faceVertexNormals.Add( ToSandboxVector3( normal ) );

		if ( sourceUvs is not null && vertexIndex < sourceUvs.Count )
		{
			var uv = sourceUvs[vertexIndex];
			faceVertexUvs.Add( new global::Vector2( uv.X, uv.Y ) );
		}
		else
		{
			faceVertexUvs.Add( global::Vector2.Zero );
		}
	}

	static CastModelTransform GetCastModelTransform( ModelNode model )
	{
		var position = model.Properties.ContainsKey( "p" ) ? model.Position : NumericsVector3.Zero;
		var rotation = model.Properties.ContainsKey( "r" ) ? model.Rotation : NumericsQuaternion.Identity;
		var scale = model.Properties.ContainsKey( "s" ) ? model.Scale : NumericsVector3.One;

		if ( !TryNormalizeCastQuaternion( rotation, out rotation ) )
			rotation = NumericsQuaternion.Identity;

		var rotationMatrix = NumericsMatrix4x4.CreateFromQuaternion( rotation );
		var normalTransform = NumericsMatrix4x4.CreateScale( scale ) * rotationMatrix;
		if ( NumericsMatrix4x4.Invert( normalTransform, out var inverseNormalTransform ) )
			normalTransform = NumericsMatrix4x4.Transpose( inverseNormalTransform );
		else
			normalTransform = rotationMatrix;

		return new CastModelTransform( position, rotation, scale, normalTransform );
	}

	static NumericsVector3 TransformCastPosition( NumericsVector3 position, CastModelTransform transform )
	{
		position *= transform.Scale;
		position = NumericsVector3.Transform( position, transform.Rotation );
		return position + transform.Position;
	}

	static bool IsValidCastFaceIndex( int index, int vertexCount )
	{
		return index >= 0 && index < vertexCount;
	}

	static bool TryNormalizeCastVector( NumericsVector3 vector, out NumericsVector3 normal )
	{
		var lengthSquared = vector.LengthSquared();
		if ( lengthSquared <= CastDegenerateTriangleEpsilon || float.IsNaN( lengthSquared ) || float.IsInfinity( lengthSquared ) )
		{
			normal = NumericsVector3.Zero;
			return false;
		}

		normal = vector / MathF.Sqrt( lengthSquared );
		return true;
	}

	static bool TryNormalizeCastQuaternion( NumericsQuaternion quaternion, out NumericsQuaternion normalized )
	{
		var lengthSquared = quaternion.LengthSquared();
		if ( lengthSquared <= CastDegenerateTriangleEpsilon || float.IsNaN( lengthSquared ) || float.IsInfinity( lengthSquared ) )
		{
			normalized = NumericsQuaternion.Identity;
			return false;
		}

		normalized = NumericsQuaternion.Normalize( quaternion );
		return true;
	}

	static global::Vector3 ToSandboxVector3( NumericsVector3 vector )
	{
		return new global::Vector3( vector.X, vector.Y, vector.Z );
	}

	static string GetCastMeshMaterial( MeshNode mesh )
	{
		string materialName = null;

		try
		{
			materialName = mesh.Material?.Name;
		}
		catch
		{
			// Missing or non-standard material names are expected in external CAST files.
		}

		if ( string.IsNullOrWhiteSpace( materialName ) )
			return CastFallbackMaterial;

		materialName = materialName.Trim().Replace( '\\', '/' ).TrimStart( '/' );
		if ( materialName.Contains( ':' ) )
			return CastFallbackMaterial;

		return materialName.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase )
			? materialName
			: CastFallbackMaterial;
	}

	static string GetCastMeshName( MeshNode mesh )
	{
		return string.IsNullOrWhiteSpace( mesh.Name ) ? "<unnamed>" : mesh.Name;
	}

	static int ToModelDocCollisionType( CreateModelFromMeshDialog.CollisionMode collisionMode )
	{
		return collisionMode switch
		{
			CreateModelFromMeshDialog.CollisionMode.None => 0,
			CreateModelFromMeshDialog.CollisionMode.Mesh => 1,
			CreateModelFromMeshDialog.CollisionMode.Hull => 2,
			_ => 0
		};
	}

	readonly struct CastModelTransform
	{
		public CastModelTransform( NumericsVector3 position, NumericsQuaternion rotation, NumericsVector3 scale, NumericsMatrix4x4 normalTransform )
		{
			Position = position;
			Rotation = rotation;
			Scale = scale;
			NormalTransform = normalTransform;
		}

		public NumericsVector3 Position { get; }
		public NumericsQuaternion Rotation { get; }
		public NumericsVector3 Scale { get; }
		public NumericsMatrix4x4 NormalTransform { get; }
	}
}
