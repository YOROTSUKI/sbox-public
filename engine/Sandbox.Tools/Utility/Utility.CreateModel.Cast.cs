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
		public ulong SourceHash { get; init; }
		public string Name { get; init; } = string.Empty;
		public string MaterialName { get; init; } = CastFallbackMaterial;
		public global::Vector3[] Positions { get; init; } = [];
		public CastTriangle[] Faces { get; init; } = [];
		public global::Vector3[] Normals { get; init; } = [];
		public global::Vector2[] TexCoords { get; init; } = [];
		public global::Vector3[] FaceVertexNormals { get; init; } = [];
		public global::Vector2[] FaceVertexTexCoords { get; init; } = [];
		public Color32[] BlendIndices { get; init; } = [];
		public Color32[] BlendWeights { get; init; } = [];
		public bool UsesSkinning { get; init; }
		public int SkippedOutOfRangeFaces { get; init; }
		public int SkippedDegenerateFaces { get; init; }
	}

	internal readonly record struct CastTriangle( int A, int B, int C );

	/// <summary>
	/// Create a vmdl file from a CAST model. Static-only files preserve the existing ModelDoc path;
	/// skeleton, skinning and animation-capable files use the animated CAST import path.
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

		var castSummary = InspectCastFile( sourcePath );
		if ( castSummary.HasSkeleton || castSummary.HasWeightData || castSummary.HasAnimation )
			return CreateAnimatedModelFromCastFiles( castFile, Array.Empty<Asset>(), targetAbsolutePath, collisionMode );

		return CreateStaticModelFromCastFile( cast, sourcePath, targetAbsolutePath, collisionMode );
	}

	static unsafe Asset CreateStaticModelFromCastFile( Cast.NET.Cast cast, string sourcePath, string targetAbsolutePath, CreateModelFromMeshDialog.CollisionMode collisionMode )
	{
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

	static SkeletonNode TryGetCastModelSkeleton( ModelNode model )
	{
		try
		{
			return model?.Skeleton;
		}
		catch
		{
			return null;
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

		fixed ( global::Vector3* pNormals = &meshData.FaceVertexNormals[0] )
			modelMesh.SetNormals( (IntPtr)pNormals, meshData.FaceVertexNormals.Length );

		fixed ( global::Vector2* pUvs = &meshData.FaceVertexTexCoords[0] )
			modelMesh.SetTexCoords( (IntPtr)pUvs, meshData.FaceVertexTexCoords.Length );

		return true;
	}

	internal static bool TryCreateCastMeshData( ModelNode model, MeshNode castMesh, out CastMeshData meshData )
	{
		var context = new CastImportContext( model?.Name ?? castMesh?.Name ?? "<cast>" );
		try
		{
			return TryCreateCastMeshData( model, castMesh, null, context, out meshData );
		}
		finally
		{
			context.FlushWarnings();
		}
	}

	static bool TryCreateCastMeshData( ModelNode model, MeshNode castMesh, CastSkeletonData skeletonData, CastImportContext context, out CastMeshData meshData )
	{
		meshData = null;

		if ( !castMesh.Properties.TryGetValue( "vp", out _ ) )
		{
			context.Warn( $"Skipping CAST mesh \"{GetCastMeshName( castMesh )}\" because it has no vertex position buffer." );
			return false;
		}

		if ( !castMesh.Properties.TryGetValue( "f", out _ ) )
		{
			context.Warn( $"Skipping CAST mesh \"{GetCastMeshName( castMesh )}\" because it has no face buffer." );
			return false;
		}

		var sourcePositions = castMesh.VertexPositionBuffer.Values;
		if ( sourcePositions.Count == 0 )
		{
			context.Warn( $"Skipping CAST mesh \"{GetCastMeshName( castMesh )}\" because it has no vertices." );
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
			context.Warn( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has no normals; triangle normals will be generated." );
		else if ( sourceNormals.Count < sourcePositions.Count )
			context.Warn( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has fewer normals than vertices; missing normals will be generated." );

		if ( sourceUvs is null || sourceUvs.Count == 0 )
			context.Warn( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has no UV0 layer; zero UVs will be used." );
		else if ( sourceUvs.Count < sourcePositions.Count )
			context.Warn( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has fewer UV0 coordinates than vertices; missing UVs will use zero." );

		var faces = new List<CastTriangle>();
		var accumulatedNormals = new NumericsVector3[sourcePositions.Count];
		var faceVertexNormals = new List<global::Vector3>();
		var faceVertexTexCoords = new List<global::Vector2>();
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
			accumulatedNormals[a] += faceNormal;
			accumulatedNormals[b] += faceNormal;
			accumulatedNormals[c] += faceNormal;
			AddFaceVertexData( a, sourceNormals, sourceUvs, transform, faceNormal, faceVertexNormals, faceVertexTexCoords );
			AddFaceVertexData( b, sourceNormals, sourceUvs, transform, faceNormal, faceVertexNormals, faceVertexTexCoords );
			AddFaceVertexData( c, sourceNormals, sourceUvs, transform, faceNormal, faceVertexNormals, faceVertexTexCoords );
		}

		if ( skippedOutOfRangeFaces > 0 )
			context.Warn( $"CAST mesh \"{GetCastMeshName( castMesh )}\" skipped {skippedOutOfRangeFaces} face(s) with out-of-range indices." );

		if ( skippedDegenerateFaces > 0 )
			context.Warn( $"CAST mesh \"{GetCastMeshName( castMesh )}\" skipped {skippedDegenerateFaces} degenerate face(s)." );

		if ( faces.Count == 0 )
		{
			context.Warn( $"Skipping CAST mesh \"{GetCastMeshName( castMesh )}\" because it has no valid triangle faces." );
			return false;
		}

		var normals = new global::Vector3[sourcePositions.Count];
		var texCoords = new global::Vector2[sourcePositions.Count];

		for ( var i = 0; i < sourcePositions.Count; i++ )
		{
			var normal = accumulatedNormals[i];
			if ( sourceNormals is not null && i < sourceNormals.Count )
			{
				var transformedNormal = NumericsVector3.TransformNormal( sourceNormals[i], transform.NormalTransform );
				if ( TryNormalizeCastVector( transformedNormal, out var normalizedNormal ) )
					normal = normalizedNormal;
			}

			if ( !TryNormalizeCastVector( normal, out normal ) )
				normal = NumericsVector3.UnitZ;

			normals[i] = ToSandboxVector3( normal );

			if ( sourceUvs is not null && i < sourceUvs.Count )
			{
				var uv = sourceUvs[i];
				texCoords[i] = new global::Vector2( uv.X, uv.Y );
			}
			else
			{
				texCoords[i] = global::Vector2.Zero;
			}
		}

		var blendIndices = Array.Empty<Color32>();
		var blendWeights = Array.Empty<Color32>();
		var usesSkinning = false;

		if ( skeletonData is not null && skeletonData.Bones.Length > 0 )
		{
			usesSkinning = true;
			TryCreateCastSkinningData( castMesh, sourcePositions.Count, skeletonData, context, out blendIndices, out blendWeights );
		}

		meshData = new CastMeshData
		{
			SourceHash = castMesh.Hash,
			Name = GetCastMeshName( castMesh ),
			MaterialName = GetCastMeshMaterial( castMesh ),
			Positions = positions,
			Faces = faces.ToArray(),
			Normals = normals,
			TexCoords = texCoords,
			FaceVertexNormals = faceVertexNormals.ToArray(),
			FaceVertexTexCoords = faceVertexTexCoords.ToArray(),
			BlendIndices = blendIndices,
			BlendWeights = blendWeights,
			UsesSkinning = usesSkinning,
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
		List<global::Vector2> faceVertexTexCoords )
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
			faceVertexTexCoords.Add( new global::Vector2( uv.X, uv.Y ) );
		}
		else
		{
			faceVertexTexCoords.Add( global::Vector2.Zero );
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

	static void TryCreateCastSkinningData(
		MeshNode castMesh,
		int vertexCount,
		CastSkeletonData skeletonData,
		CastImportContext context,
		out Color32[] blendIndices,
		out Color32[] blendWeights )
	{
		blendIndices = new Color32[vertexCount];
		blendWeights = new Color32[vertexCount];

		if ( castMesh.VertexWeightBoneBuffer is null || castMesh.VertexWeightValueBuffer is null )
		{
			context.Warn( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has a skeleton but no weight buffers; using root-bone weights." );
			FillDefaultCastWeights( blendIndices, blendWeights );
			return;
		}

		var maximumInfluence = castMesh.Properties.ContainsKey( "mi" ) ? castMesh.MaximumWeightInfluence : 0;
		var weightCount = castMesh.VertexWeightValueBuffer.ValueCount;

		if ( maximumInfluence <= 0 )
		{
			if ( vertexCount > 0 && (weightCount % vertexCount) == 0 )
				maximumInfluence = weightCount / vertexCount;
			else
				maximumInfluence = 1;
		}

		if ( maximumInfluence <= 0 || (vertexCount * maximumInfluence) > weightCount || castMesh.VertexWeightBoneBuffer.ValueCount < (vertexCount * maximumInfluence) )
		{
			context.Warn( $"CAST mesh \"{GetCastMeshName( castMesh )}\" has mismatched skinning buffers; using root-bone weights." );
			FillDefaultCastWeights( blendIndices, blendWeights );
			return;
		}

		var allWeights = castMesh.EnumerateBoneWeights().ToArray();
		for ( var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++ )
		{
			var start = vertexIndex * maximumInfluence;
			var influences = new List<(int BoneIndex, float Weight)>( maximumInfluence );

			for ( var influenceIndex = 0; influenceIndex < maximumInfluence; influenceIndex++ )
			{
				var weightEntry = allWeights[start + influenceIndex];
				if ( !IsValidCastBoneIndex( weightEntry.Item1, skeletonData.Bones.Length ) )
					continue;

				var weightValue = weightEntry.Item2;
				if ( weightValue <= 0.0f || float.IsNaN( weightValue ) || float.IsInfinity( weightValue ) )
					continue;

				influences.Add( (weightEntry.Item1, weightValue) );
			}

			if ( influences.Count == 0 )
			{
				blendIndices[vertexIndex] = new Color32( 0, 0, 0, 0 );
				blendWeights[vertexIndex] = new Color32( 255, 0, 0, 0 );
				continue;
			}

			influences.Sort( static ( a, b ) => b.Weight.CompareTo( a.Weight ) );
			if ( influences.Count > 4 )
				influences.RemoveRange( 4, influences.Count - 4 );

			var totalWeight = influences.Sum( x => x.Weight );
			if ( totalWeight <= 0.0f )
			{
				blendIndices[vertexIndex] = new Color32( 0, 0, 0, 0 );
				blendWeights[vertexIndex] = new Color32( 255, 0, 0, 0 );
				continue;
			}

			var normalizedWeights = new int[4];
			var normalizedBones = new byte[4];
			for ( var i = 0; i < influences.Count; i++ )
			{
				normalizedBones[i] = (byte)influences[i].BoneIndex;
				normalizedWeights[i] = (int)MathF.Round( influences[i].Weight / totalWeight * 255.0f );
			}

			var weightDelta = 255 - normalizedWeights.Sum();
			var dominantIndex = Array.IndexOf( normalizedWeights, normalizedWeights.Max() );
			if ( dominantIndex >= 0 )
				normalizedWeights[dominantIndex] += weightDelta;

			blendIndices[vertexIndex] = new Color32( normalizedBones[0], normalizedBones[1], normalizedBones[2], normalizedBones[3] );
			blendWeights[vertexIndex] = new Color32(
				(byte)Math.Clamp( normalizedWeights[0], 0, 255 ),
				(byte)Math.Clamp( normalizedWeights[1], 0, 255 ),
				(byte)Math.Clamp( normalizedWeights[2], 0, 255 ),
				(byte)Math.Clamp( normalizedWeights[3], 0, 255 ) );
		}
	}

	static void FillDefaultCastWeights( Color32[] blendIndices, Color32[] blendWeights )
	{
		for ( var i = 0; i < blendIndices.Length; i++ )
		{
			blendIndices[i] = new Color32( 0, 0, 0, 0 );
			blendWeights[i] = new Color32( 255, 0, 0, 0 );
		}
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

	static bool IsValidCastBoneIndex( int index, int boneCount )
	{
		return index >= 0 && index < boneCount;
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
