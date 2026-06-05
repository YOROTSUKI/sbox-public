using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Cast.NET;
using Cast.NET.Nodes;

using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace Editor;

public enum CastAdvancedDataMode
{
	BasicOnly,
	AdvancedWhenSupported,
	StrictAdvanced
}

public enum CastRootMotionMode
{
	Auto,
	None,
	Bone
}

public sealed class CastAnimatedModelImportOptions
{
	public CastAdvancedDataMode AdvancedDataMode { get; set; } = CastAdvancedDataMode.BasicOnly;
	public CastRootMotionMode RootMotionMode { get; set; } = CastRootMotionMode.Auto;
	public string RootMotionBoneName { get; set; } = string.Empty;

	public static CastAnimatedModelImportOptions BasicOnly => new();
}

public static partial class EditorUtility
{
	internal sealed class CastImportContext
	{
		readonly HashSet<string> _warningSet = new( StringComparer.Ordinal );

		public CastImportContext( string sourcePath )
		{
			SourcePath = sourcePath;
		}

		public string SourcePath { get; }
		public List<string> Warnings { get; } = [];

		public void Warn( string message )
		{
			if ( _warningSet.Add( message ) )
				Warnings.Add( message );
		}

		public void FlushWarnings()
		{
			foreach ( var warning in Warnings )
				Log.Warning( warning );
		}
	}

	internal sealed class CastSourceData
	{
		public string Name { get; init; } = string.Empty;
		public CastSkeletonData Skeleton { get; init; }
		public CastMeshData[] Meshes { get; init; } = [];
		public CastBlendShapeData[] BlendShapes { get; init; } = [];
		public CastIkHandleData[] IkHandles { get; init; } = [];
		public CastConstraintData[] Constraints { get; init; } = [];
	}

	internal sealed class CastAnimatedImportData
	{
		public CastSourceData SourceData { get; init; }
		public List<CastAnimationData> Animations { get; init; } = [];
	}

	internal sealed class CastSkeletonData
	{
		public CastBoneData[] Bones { get; init; } = [];
		public IReadOnlyDictionary<string, int> BoneIndicesByName { get; init; } = new Dictionary<string, int>( StringComparer.Ordinal );
		public IReadOnlyDictionary<ulong, int> BoneIndicesByHash { get; init; } = new Dictionary<ulong, int>();
	}

	internal readonly record struct CastBoneData( string Name, int ParentIndex, Transform LocalTransform, ulong SourceHash );

	internal sealed class CastAnimationData
	{
		public string Name { get; init; } = string.Empty;
		public string SourcePath { get; init; } = string.Empty;
		public float FrameRate { get; init; } = 30.0f;
		public bool Looping { get; init; }
		public bool HasScaleKeys { get; init; }
		public IReadOnlyList<CastAnimationEventData> Events { get; init; } = [];
		public CastRootMotionData? RootMotion { get; set; }
		public CastAnimationFrameData[] Frames { get; init; } = [];
	}

	internal sealed class CastAnimationFrameData
	{
		public Transform[] BoneTransforms { get; init; } = [];
	}

	internal readonly record struct CastAnimationEventData( string Name, int Frame );

	internal readonly record struct CastRootMotionData( string BoneName, int BoneIndex );

	internal sealed class CastBlendShapeData
	{
		public ulong SourceHash { get; init; }
		public string Name { get; init; } = string.Empty;
		public ulong BaseMeshHash { get; init; }
		public string BaseMeshName { get; init; } = string.Empty;
		public float Weight { get; init; } = 1.0f;
		public CastBlendShapeVertexDelta[] VertexDeltas { get; init; } = [];
	}

	internal readonly record struct CastBlendShapeVertexDelta( int VertexIndex, Vector3 Delta );

	internal sealed class CastIkHandleData
	{
		public string Name { get; init; } = string.Empty;
		public int StartBoneIndex { get; init; }
		public int EndBoneIndex { get; init; }
		public int TargetBoneIndex { get; init; } = -1;
		public int PoleVectorBoneIndex { get; init; } = -1;
		public int PoleBoneIndex { get; init; } = -1;
		public bool UseTargetRotation { get; init; }
	}

	internal sealed class CastConstraintData
	{
		public string Name { get; init; } = string.Empty;
		public string ConstraintType { get; init; } = string.Empty;
		public int ConstraintBoneIndex { get; init; }
		public int TargetBoneIndex { get; init; }
		public bool MaintainOffset { get; init; }
		public NumericsVector4 CustomOffset { get; init; }
		public float Weight { get; init; }
		public bool SkipX { get; init; }
		public bool SkipY { get; init; }
		public bool SkipZ { get; init; }
	}

	internal readonly record struct CastFileSummary( bool HasModelNode, bool HasMesh, bool HasSkeleton, bool HasWeightData, bool HasAnimation );

	internal enum CastAnimationMode
	{
		Absolute,
		Relative,
		Additive
	}

	internal sealed class CastCurveTrack<T> where T : unmanaged
	{
		public CastCurveTrack( int[] frames, T[] values )
		{
			Frames = frames;
			Values = values;
		}

		public int[] Frames { get; }
		public T[] Values { get; }
		public int MaxFrame => Frames.Length > 0 ? Frames[^1] : -1;

		public bool TrySample( int frame, out T value )
		{
			if ( Frames.Length == 0 )
			{
				value = default;
				return false;
			}

			var index = Array.BinarySearch( Frames, frame );
			if ( index < 0 )
			{
				index = ~index - 1;
			}
			else
			{
				while ( index + 1 < Frames.Length && Frames[index + 1] == frame )
					index++;
			}

			if ( index < 0 )
			{
				value = default;
				return false;
			}

			value = Values[index];
			return true;
		}
	}

	internal sealed class CastBoneCurveData
	{
		public int BoneIndex { get; init; }
		public string BoneName { get; init; } = string.Empty;

		public CastAnimationMode? PositionMode { get; set; }
		public CastAnimationMode? RotationMode { get; set; }
		public CastAnimationMode? ScaleMode { get; set; }

		public CastCurveTrack<float> PositionX { get; set; }
		public CastCurveTrack<float> PositionY { get; set; }
		public CastCurveTrack<float> PositionZ { get; set; }
		public CastCurveTrack<NumericsVector3> Position { get; set; }

		public CastCurveTrack<NumericsQuaternion> Rotation { get; set; }

		public CastCurveTrack<float> ScaleX { get; set; }
		public CastCurveTrack<float> ScaleY { get; set; }
		public CastCurveTrack<float> ScaleZ { get; set; }
		public CastCurveTrack<NumericsVector3> Scale { get; set; }

		public bool HasAnyCurves =>
			PositionX is not null || PositionY is not null || PositionZ is not null || Position is not null ||
			Rotation is not null ||
			ScaleX is not null || ScaleY is not null || ScaleZ is not null || Scale is not null;

		public int MaxFrame
		{
			get
			{
				var max = -1;
				max = Math.Max( max, PositionX?.MaxFrame ?? -1 );
				max = Math.Max( max, PositionY?.MaxFrame ?? -1 );
				max = Math.Max( max, PositionZ?.MaxFrame ?? -1 );
				max = Math.Max( max, Position?.MaxFrame ?? -1 );
				max = Math.Max( max, Rotation?.MaxFrame ?? -1 );
				max = Math.Max( max, ScaleX?.MaxFrame ?? -1 );
				max = Math.Max( max, ScaleY?.MaxFrame ?? -1 );
				max = Math.Max( max, ScaleZ?.MaxFrame ?? -1 );
				max = Math.Max( max, Scale?.MaxFrame ?? -1 );
				return max;
			}
		}
	}

	[StructLayout( LayoutKind.Sequential )]
	internal struct CastSkinnedVertex( Vector3 position, Vector3 normal, Vector3 tangent, Vector2 texcoord, Color32 blendIndices, Color32 blendWeights )
	{
		[VertexLayout.Position]
		public Vector3 position = position;

		[VertexLayout.Normal]
		public Vector3 normal = normal;

		[VertexLayout.Tangent]
		public Vector3 tangent = tangent;

		[VertexLayout.TexCoord]
		public Vector2 texcoord = texcoord;

		[VertexLayout.BlendIndices]
		public Color32 blendIndices = blendIndices;

		[VertexLayout.BlendWeight]
		public Color32 blendWeights = blendWeights;
	}

	static readonly UTF8Encoding Utf8WithoutBom = new( false );

	public static Asset CreateAnimatedModelFromCastFiles(
		Asset modelCastFile,
		IReadOnlyList<Asset> animationCastFiles,
		string targetAbsolutePath,
		CreateModelFromMeshDialog.CollisionMode collisionMode )
	{
		return CreateAnimatedModelFromCastFiles(
			modelCastFile,
			animationCastFiles,
			targetAbsolutePath,
			collisionMode,
			CastAnimatedModelImportOptions.BasicOnly );
	}

	public static Asset CreateAnimatedModelFromCastFiles(
		Asset modelCastFile,
		IReadOnlyList<Asset> animationCastFiles,
		string targetAbsolutePath,
		CreateModelFromMeshDialog.CollisionMode collisionMode,
		CastAnimatedModelImportOptions options )
	{
		return CreateAnimatedModelFromCastFiles(
			modelCastFile,
			animationCastFiles,
			targetAbsolutePath,
			collisionMode,
			options,
			Array.Empty<Asset>() );
	}

	public static Asset CreateAnimatedModelFromCastFiles(
		Asset modelCastFile,
		IReadOnlyList<Asset> animationCastFiles,
		string targetAbsolutePath,
		CreateModelFromMeshDialog.CollisionMode collisionMode,
		CastAnimatedModelImportOptions options,
		IReadOnlyList<Asset> rigsetFiles )
	{
		if ( modelCastFile is null )
			return null;

		options ??= CastAnimatedModelImportOptions.BasicOnly;

		if ( string.IsNullOrWhiteSpace( targetAbsolutePath ) )
			return null;

		var sourcePath = modelCastFile.AbsolutePath;
		if ( string.IsNullOrWhiteSpace( sourcePath ) || !File.Exists( sourcePath ) )
			return null;

		var context = new CastImportContext( sourcePath );
		try
		{
			if ( !TryCollectCastAnimatedImportData( sourcePath, GetCastAssetPaths( animationCastFiles ), options, context, out var importData ) )
				return null;

			var rigsets = LoadRigsetReferences( rigsetFiles, context );
			if ( !TryWriteCastModel( importData.SourceData, importData.Animations, targetAbsolutePath, collisionMode, options, context, rigsets ) )
				return null;

			var asset = AssetSystem.RegisterFile( targetAbsolutePath );
			if ( asset is null )
				return null;

			if ( !asset.Compile( true ) || asset.IsCompileFailed || !WaitForCompiledCastModelAsset( asset ) )
			{
				context.Warn( $"Failed to compile generated CAST model \"{targetAbsolutePath}\"." );
				return null;
			}

			return asset;
		}
		finally
		{
			context.FlushWarnings();
		}
	}

	static IReadOnlyList<string> GetCastAssetPaths( IReadOnlyList<Asset> castFiles )
	{
		if ( castFiles is null || castFiles.Count == 0 )
			return [];

		var paths = new List<string>( castFiles.Count );
		foreach ( var asset in castFiles )
		{
			if ( asset is not null && !string.IsNullOrWhiteSpace( asset.AbsolutePath ) )
				paths.Add( asset.AbsolutePath );
		}

		return paths;
	}

	internal static bool TryCollectCastAnimatedImportData(
		string baseCastPath,
		IReadOnlyList<string> animationCastPaths,
		CastAnimatedModelImportOptions options,
		CastImportContext context,
		out CastAnimatedImportData importData )
	{
		importData = null;
		options ??= CastAnimatedModelImportOptions.BasicOnly;

		if ( string.IsNullOrWhiteSpace( baseCastPath ) || !File.Exists( baseCastPath ) )
			return false;

		if ( !TryLoadCastFile( baseCastPath, context, out var baseCast ) )
			return false;

		if ( !TryCreateCastSourceData( baseCast, Path.GetFileNameWithoutExtension( baseCastPath ), context, out var sourceData ) )
			return false;

		var animations = new List<CastAnimationData>();
		var usedAnimationNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		if ( sourceData.Skeleton is not null )
		{
			AddCastAnimations(
				baseCast,
				sourceData.Skeleton,
				Path.GetFileNameWithoutExtension( baseCastPath ),
				context,
				animations,
				usedAnimationNames,
				baseCastPath );
		}

		if ( sourceData.Skeleton is not null && animationCastPaths is not null )
		{
			for ( var i = 0; i < animationCastPaths.Count; i++ )
			{
				var animationPath = animationCastPaths[i];
				if ( string.IsNullOrWhiteSpace( animationPath ) || !File.Exists( animationPath ) )
					continue;

				if ( string.Equals( animationPath, baseCastPath, StringComparison.OrdinalIgnoreCase ) )
					continue;

				if ( !TryLoadCastFile( animationPath, context, out var animationCast ) )
					continue;

				var fallbackName = Path.GetFileNameWithoutExtension( animationPath );
				AddCastAnimations( animationCast, sourceData.Skeleton, fallbackName, context, animations, usedAnimationNames, animationPath );
			}
		}

		if ( !TryApplyCastRootMotionOptions( sourceData.Skeleton, animations, options, context ) )
			return false;

		importData = new CastAnimatedImportData
		{
			SourceData = sourceData,
			Animations = animations
		};
		return true;
	}

	internal static CastFileSummary InspectCastFile( string sourcePath )
	{
		try
		{
			var cast = CastReader.Load( sourcePath );
			var modelNodes = EnumerateCastModels( cast ).ToArray();
			var meshes = modelNodes.SelectMany( x => x.EnumerateMeshes() ).ToArray();
			var hasSkeleton = modelNodes.Any( x => TryGetCastModelSkeleton( x ) is not null ) || EnumerateCastNodes<SkeletonNode>( cast ).Any();
			var hasWeights = meshes.Any( x => x.VertexWeightBoneBuffer is not null && x.VertexWeightValueBuffer is not null );

			return new CastFileSummary(
				HasModelNode: modelNodes.Length > 0,
				HasMesh: meshes.Length > 0,
				HasSkeleton: hasSkeleton,
				HasWeightData: hasWeights,
				HasAnimation: EnumerateCastAnimations( cast ).Any() );
		}
		catch
		{
			return default;
		}
	}

	internal static string[] InspectCastSkeletonBoneNames( string sourcePath )
	{
		try
		{
			var cast = CastReader.Load( sourcePath );
			var modelNodes = EnumerateCastModels( cast ).ToArray();
			var skeletonNode = modelNodes.Select( TryGetCastModelSkeleton ).FirstOrDefault( x => x is not null ) ??
				EnumerateCastNodes<SkeletonNode>( cast ).FirstOrDefault();

			if ( skeletonNode is null )
				return [];

			return skeletonNode
				.EnumerateBones()
				.Select( ( bone, index ) => string.IsNullOrWhiteSpace( bone.Name ) ? $"bone_{index}" : bone.Name )
				.ToArray();
		}
		catch
		{
			return [];
		}
	}

	static bool TryLoadCastFile( string sourcePath, CastImportContext context, out Cast.NET.Cast cast )
	{
		try
		{
			cast = CastReader.Load( sourcePath );
			return true;
		}
		catch ( Exception ex )
		{
			context.Warn( $"Failed to load CAST file \"{sourcePath}\": {ex.Message}" );
			cast = null;
			return false;
		}
	}

	internal static bool TryCreateCastSourceData( Cast.NET.Cast cast, string sourceName, CastImportContext context, out CastSourceData sourceData )
	{
		sourceData = null;

		var models = EnumerateCastModels( cast ).ToArray();
		var meshes = new List<CastMeshData>();
		var meshesByHash = new Dictionary<ulong, CastMeshData>();
		var blendShapes = new List<CastBlendShapeData>();

		var skeletonModel = models.FirstOrDefault( x => TryGetCastModelSkeleton( x ) is not null );
		var skeletonNode = skeletonModel is not null ? TryGetCastModelSkeleton( skeletonModel ) : EnumerateCastNodes<SkeletonNode>( cast ).FirstOrDefault();
		CastSkeletonData skeletonData = null;

		if ( skeletonNode is not null )
		{
			var skeletonTransform = skeletonModel is not null ? GetCastModelTransform( skeletonModel ) : default;
			if ( !TryCreateCastSkeletonData( skeletonNode, skeletonTransform, context, out skeletonData ) )
				return false;
		}

		foreach ( var model in models )
		{
			foreach ( var castMesh in model.EnumerateMeshes() )
			{
				if ( TryCreateCastMeshData( model, castMesh, skeletonData, context, out var meshData ) )
				{
					meshes.Add( meshData );

					if ( meshData.SourceHash != 0 && !meshesByHash.ContainsKey( meshData.SourceHash ) )
						meshesByHash.Add( meshData.SourceHash, meshData );
				}
			}
		}

		foreach ( var model in models )
		{
			var modelTransform = GetCastModelTransform( model );
			foreach ( var blendShape in model.EnumerateBlendShapes() )
			{
				if ( TryCreateCastBlendShapeData( blendShape, meshesByHash, modelTransform, context, out var blendShapeData ) )
					blendShapes.Add( blendShapeData );
			}
		}

		CastIkHandleData[] ikHandles = skeletonNode is not null && skeletonData is not null
			? CreateCastIkHandleData( skeletonNode, skeletonData, context )
			: [];
		CastConstraintData[] constraints = skeletonNode is not null && skeletonData is not null
			? CreateCastConstraintData( skeletonNode, skeletonData, context )
			: [];

		if ( skeletonData is null && meshes.Count == 0 )
		{
			context.Warn( $"CAST file \"{context.SourcePath}\" did not contain any usable mesh or skeleton data." );
			return false;
		}

		sourceData = new CastSourceData
		{
			Name = sourceName,
			Skeleton = skeletonData,
			Meshes = meshes.ToArray(),
			BlendShapes = blendShapes.ToArray(),
			IkHandles = ikHandles,
			Constraints = constraints
		};

		return true;
	}

	static bool TryCreateCastBlendShapeData(
		BlendShapeNode blendShape,
		IReadOnlyDictionary<ulong, CastMeshData> meshesByHash,
		CastModelTransform modelTransform,
		CastImportContext context,
		out CastBlendShapeData blendShapeData )
	{
		blendShapeData = null;

		if ( blendShape is null )
			return false;

		ulong baseShapeHash;
		try
		{
			baseShapeHash = blendShape.BaseShapeHash;
		}
		catch ( Exception ex )
		{
			context.Warn( $"Skipping CAST blend shape \"{GetCastBlendShapeName( blendShape )}\": {ex.Message}" );
			return false;
		}

		if ( !meshesByHash.TryGetValue( baseShapeHash, out var baseMesh ) )
		{
			context.Warn( $"Skipping CAST blend shape \"{GetCastBlendShapeName( blendShape )}\" because base mesh hash 0x{baseShapeHash:X16} was not imported." );
			return false;
		}

		var deltas = new List<CastBlendShapeVertexDelta>();
		try
		{
			foreach ( var (vertexIndex, targetPosition) in blendShape.EnumerateVertices() )
			{
				if ( vertexIndex < 0 || vertexIndex >= baseMesh.Positions.Length )
				{
					context.Warn( $"Skipping CAST blend shape \"{GetCastBlendShapeName( blendShape )}\" vertex {vertexIndex} because it is outside base mesh \"{baseMesh.Name}\"." );
					continue;
				}

				if ( !IsFinite( targetPosition ) )
				{
					context.Warn( $"Skipping invalid CAST blend shape \"{GetCastBlendShapeName( blendShape )}\" vertex {vertexIndex}." );
					continue;
				}

				var transformedTarget = ToSandboxVector3( TransformCastPosition( targetPosition, modelTransform ) );
				deltas.Add( new CastBlendShapeVertexDelta( vertexIndex, transformedTarget - baseMesh.Positions[vertexIndex] ) );
			}
		}
		catch ( Exception ex )
		{
			context.Warn( $"Skipping CAST blend shape \"{GetCastBlendShapeName( blendShape )}\": {ex.Message}" );
			return false;
		}

		if ( deltas.Count == 0 )
		{
			context.Warn( $"Skipping CAST blend shape \"{GetCastBlendShapeName( blendShape )}\" because it did not contain any usable vertex deltas." );
			return false;
		}

		var weight = blendShape.Weight;
		if ( float.IsNaN( weight ) || float.IsInfinity( weight ) )
			weight = 1.0f;

		blendShapeData = new CastBlendShapeData
		{
			SourceHash = blendShape.Hash,
			Name = GetCastBlendShapeName( blendShape ),
			BaseMeshHash = baseShapeHash,
			BaseMeshName = baseMesh.Name,
			Weight = weight,
			VertexDeltas = deltas.ToArray()
		};
		return true;
	}

	static CastIkHandleData[] CreateCastIkHandleData( SkeletonNode skeletonNode, CastSkeletonData skeletonData, CastImportContext context )
	{
		var handles = new List<CastIkHandleData>();

		foreach ( var handle in skeletonNode.EnumerateIKHandles() )
		{
			var handleName = GetCastIkHandleName( handle, handles.Count );
			try
			{
				if ( !TryResolveRequiredCastBoneHash( skeletonData, handle.StartBoneHash, context, "IK handle", handleName, "start", out var startBoneIndex ) ||
					!TryResolveRequiredCastBoneHash( skeletonData, handle.EndBoneHash, context, "IK handle", handleName, "end", out var endBoneIndex ) )
					continue;

				TryResolveOptionalCastBoneHash( skeletonData, handle.TargetBoneHash, context, "IK handle", handleName, "target", out var targetBoneIndex );
				TryResolveOptionalCastBoneHash( skeletonData, handle.PoleVectorBoneHash, context, "IK handle", handleName, "pole vector", out var poleVectorBoneIndex );
				TryResolveOptionalCastBoneHash( skeletonData, handle.PoleBoneHash, context, "IK handle", handleName, "pole", out var poleBoneIndex );

				handles.Add( new CastIkHandleData
				{
					Name = handleName,
					StartBoneIndex = startBoneIndex,
					EndBoneIndex = endBoneIndex,
					TargetBoneIndex = targetBoneIndex,
					PoleVectorBoneIndex = poleVectorBoneIndex,
					PoleBoneIndex = poleBoneIndex,
					UseTargetRotation = handle.UseTargetRotation
				} );
			}
			catch ( Exception ex )
			{
				context.Warn( $"Skipping CAST IK handle \"{handleName}\": {ex.Message}" );
			}
		}

		return handles.ToArray();
	}

	static CastConstraintData[] CreateCastConstraintData( SkeletonNode skeletonNode, CastSkeletonData skeletonData, CastImportContext context )
	{
		var constraints = new List<CastConstraintData>();

		foreach ( var constraint in skeletonNode.EnumerateConstraints() )
		{
			var constraintName = GetCastConstraintName( constraint, constraints.Count );
			try
			{
				var constraintType = constraint.ConstraintType?.Trim();
				if ( constraintType is not ("pt" or "or" or "sc") )
				{
					context.Warn( $"Skipping CAST constraint \"{constraintName}\" because constraint type \"{constraint.ConstraintType}\" is unsupported." );
					continue;
				}

				if ( !TryResolveRequiredCastBoneHash( skeletonData, constraint.ConstraintBoneHash, context, "constraint", constraintName, "constrained", out var constraintBoneIndex ) ||
					!TryResolveRequiredCastBoneHash( skeletonData, constraint.TargetBoneHash, context, "constraint", constraintName, "target", out var targetBoneIndex ) )
					continue;

				var weight = constraint.Weight;
				if ( float.IsNaN( weight ) || float.IsInfinity( weight ) )
					weight = 1.0f;

				constraints.Add( new CastConstraintData
				{
					Name = constraintName,
					ConstraintType = constraintType,
					ConstraintBoneIndex = constraintBoneIndex,
					TargetBoneIndex = targetBoneIndex,
					MaintainOffset = constraint.MaintainOffset,
					CustomOffset = constraint.CustomOffset,
					Weight = weight,
					SkipX = constraint.SkipX,
					SkipY = constraint.SkipY,
					SkipZ = constraint.SkipZ
				} );
			}
			catch ( Exception ex )
			{
				context.Warn( $"Skipping CAST constraint \"{constraintName}\": {ex.Message}" );
			}
		}

		return constraints.ToArray();
	}

	static bool TryResolveRequiredCastBoneHash(
		CastSkeletonData skeletonData,
		ulong hash,
		CastImportContext context,
		string featureType,
		string featureName,
		string role,
		out int boneIndex )
	{
		if ( TryResolveCastBoneHash( skeletonData, hash, out boneIndex ) )
			return true;

		context.Warn( $"Skipping CAST {featureType} \"{featureName}\" because {role} bone hash 0x{hash:X16} was not imported." );
		return false;
	}

	static void TryResolveOptionalCastBoneHash(
		CastSkeletonData skeletonData,
		ulong hash,
		CastImportContext context,
		string featureType,
		string featureName,
		string role,
		out int boneIndex )
	{
		boneIndex = -1;
		if ( hash == 0 )
			return;

		if ( TryResolveCastBoneHash( skeletonData, hash, out boneIndex ) )
			return;

		context.Warn( $"CAST {featureType} \"{featureName}\" references missing optional {role} bone hash 0x{hash:X16}; that reference will be omitted." );
	}

	static bool TryResolveCastBoneHash( CastSkeletonData skeletonData, ulong hash, out int boneIndex )
	{
		boneIndex = -1;
		return hash != 0 && skeletonData.BoneIndicesByHash.TryGetValue( hash, out boneIndex );
	}

	static string GetCastBlendShapeName( BlendShapeNode blendShape )
	{
		return string.IsNullOrWhiteSpace( blendShape.Name ) ? $"blend_{blendShape.Hash:X16}" : blendShape.Name;
	}

	static string GetCastIkHandleName( IKHandleNode handle, int index )
	{
		return string.IsNullOrWhiteSpace( handle.Name ) ? $"ik_{index}" : handle.Name;
	}

	static string GetCastConstraintName( ConstraintNode constraint, int index )
	{
		return string.IsNullOrWhiteSpace( constraint.Name ) ? $"constraint_{index}" : constraint.Name;
	}

	static bool WaitForCompiledCastModelAsset( Asset asset, int timeoutMilliseconds = 10000 )
	{
		var deadline = Environment.TickCount64 + timeoutMilliseconds;
		while ( Environment.TickCount64 <= deadline )
		{
			if ( asset.IsCompileFailed )
				return false;

			var compiledPath = asset.GetCompiledFile( true );
			if ( asset.IsCompiled && asset.HasCompiledFile && !string.IsNullOrWhiteSpace( compiledPath ) && File.Exists( compiledPath ) )
				return true;

			IAssetSystem.RunFrame();
			System.Threading.Thread.Sleep( 50 );
		}

		if ( asset.IsCompileFailed )
			return false;

		var finalCompiledPath = asset.GetCompiledFile( true );
		return asset.IsCompiled && asset.HasCompiledFile && !string.IsNullOrWhiteSpace( finalCompiledPath ) && File.Exists( finalCompiledPath );
	}

	internal static bool TryCreateCastSkeletonData( SkeletonNode skeleton, out CastSkeletonData skeletonData )
	{
		return TryCreateCastSkeletonData( skeleton, default, null, out skeletonData );
	}

	static bool TryCreateCastSkeletonData( SkeletonNode skeleton, CastModelTransform modelTransform, CastImportContext context, out CastSkeletonData skeletonData )
	{
		skeletonData = null;

		if ( skeleton is null )
			return false;

		var sourceBones = skeleton.EnumerateBones().ToArray();
		if ( sourceBones.Length == 0 )
		{
			context?.Warn( "Skipping empty CAST skeleton." );
			return false;
		}

		var bones = new CastBoneData[sourceBones.Length];
		var boneIndices = new Dictionary<string, int>( sourceBones.Length, StringComparer.Ordinal );
		var boneIndicesByHash = new Dictionary<ulong, int>( sourceBones.Length );

		for ( var i = 0; i < sourceBones.Length; i++ )
		{
			var sourceBone = sourceBones[i];

			var parentIndex = sourceBone.ParentIndex;
			if ( parentIndex < 0 || parentIndex >= sourceBones.Length )
				parentIndex = -1;

			var position = sourceBone.Properties.ContainsKey( "lp" )
				? sourceBone.LocalPosition
				: sourceBone.Properties.ContainsKey( "wp" ) && parentIndex < 0
					? sourceBone.WorldPosition
					: NumericsVector3.Zero;

			var rotation = sourceBone.Properties.ContainsKey( "lr" )
				? sourceBone.LocalRotation
				: sourceBone.Properties.ContainsKey( "wr" ) && parentIndex < 0
					? sourceBone.WorldRotation
					: NumericsQuaternion.Identity;

			var scale = sourceBone.Properties.ContainsKey( "s" ) ? sourceBone.Scale : NumericsVector3.One;

			if ( !IsFinite( position ) )
			{
				context?.Warn( $"CAST bone \"{sourceBone.Name}\" has invalid local position; using zero." );
				position = NumericsVector3.Zero;
			}

			if ( !TryNormalizeCastQuaternion( rotation, out rotation ) )
			{
				context?.Warn( $"CAST bone \"{sourceBone.Name}\" has invalid local rotation; using identity." );
				rotation = NumericsQuaternion.Identity;
			}

			if ( !IsFinite( scale ) )
			{
				context?.Warn( $"CAST bone \"{sourceBone.Name}\" has invalid scale; using one." );
				scale = NumericsVector3.One;
			}

			if ( parentIndex < 0 && !IsIdentityCastModelTransform( modelTransform ) )
			{
				position = TransformCastPosition( position, modelTransform );
				rotation = modelTransform.Rotation * rotation;
				scale *= modelTransform.Scale;

				if ( !TryNormalizeCastQuaternion( rotation, out rotation ) )
					rotation = NumericsQuaternion.Identity;
			}

			var boneName = string.IsNullOrWhiteSpace( sourceBone.Name ) ? $"bone_{i}" : sourceBone.Name;
			bones[i] = new CastBoneData(
				boneName,
				parentIndex,
				new Transform( ToSandboxVector3( position ), (Rotation)rotation, ToSandboxVector3( scale ) ),
				sourceBone.Hash );
			boneIndices[boneName] = i;

			if ( sourceBone.Hash != 0 && !boneIndicesByHash.ContainsKey( sourceBone.Hash ) )
				boneIndicesByHash.Add( sourceBone.Hash, i );
		}

		skeletonData = new CastSkeletonData
		{
			Bones = bones,
			BoneIndicesByName = boneIndices,
			BoneIndicesByHash = boneIndicesByHash
		};

		return true;
	}

	internal static void AddCastAnimations(
		Cast.NET.Cast cast,
		CastSkeletonData skeletonData,
		string fallbackName,
		CastImportContext context,
		List<CastAnimationData> animations,
		HashSet<string> usedAnimationNames,
		string sourcePath = "" )
	{
		if ( cast is null || skeletonData is null || animations is null || usedAnimationNames is null )
			return;

		foreach ( var animationNode in EnumerateCastAnimations( cast ) )
		{
			var animationName = GetUniqueCastAnimationName( fallbackName, usedAnimationNames );
			if ( TryCreateCastAnimationData( animationNode, skeletonData, animationName, context, out var animationData, sourcePath ) )
			{
				animations.Add( animationData );
			}
			else
			{
				usedAnimationNames.Remove( animationName );
			}
		}
	}

	internal static bool TryCreateCastAnimationData( AnimationNode animationNode, CastSkeletonData skeletonData, string animationName, CastImportContext context, out CastAnimationData animationData, string sourcePath = "" )
	{
		animationData = null;

		if ( animationNode is null || skeletonData is null || skeletonData.Bones.Length == 0 )
			return false;

		var curvesByBone = new Dictionary<int, CastBoneCurveData>();
		var modeOverrides = animationNode
			.EnumerateCurveModeOverrides()
			.GroupBy( x => x.NodeName, StringComparer.Ordinal )
			.ToDictionary( x => x.Key, x => x.First(), StringComparer.Ordinal );
		var hasScaleKeys = false;

		foreach ( var curve in animationNode.EnumerateCurves() )
		{
			if ( !skeletonData.BoneIndicesByName.TryGetValue( curve.NodeName, out var boneIndex ) )
			{
				context.Warn( $"Skipping CAST animation curve for unknown bone \"{curve.NodeName}\"." );
				continue;
			}

			var keyName = curve.KeyPropertyName?.Trim();
			if ( string.IsNullOrWhiteSpace( keyName ) )
			{
				context.Warn( $"Skipping unnamed CAST animation curve for bone \"{curve.NodeName}\"." );
				continue;
			}

			var boneCurves = curvesByBone.TryGetValue( boneIndex, out var existing )
				? existing
				: curvesByBone[boneIndex] = new CastBoneCurveData { BoneIndex = boneIndex, BoneName = curve.NodeName };

			switch ( keyName )
			{
				case "lp":
				case "p":
					if ( !TryResolveCurveMode( curve, modeOverrides, keyName, context, out var positionMode ) ||
						!TryCreateVector3Track( curve, context, out var positionTrack ) )
						continue;

					boneCurves.PositionMode = positionMode;
					boneCurves.Position = positionTrack;
					break;

				case "tx":
				case "ty":
				case "tz":
					if ( !TryResolveCurveMode( curve, modeOverrides, keyName, context, out positionMode ) ||
						!TryCreateFloatTrack( curve, context, out var axisTrack ) )
						continue;

					boneCurves.PositionMode = positionMode;
					if ( keyName == "tx" ) boneCurves.PositionX = axisTrack;
					if ( keyName == "ty" ) boneCurves.PositionY = axisTrack;
					if ( keyName == "tz" ) boneCurves.PositionZ = axisTrack;
					break;

				case "lr":
				case "r":
				case "rq":
					if ( !TryResolveCurveMode( curve, modeOverrides, keyName, context, out var rotationMode ) ||
						!TryCreateQuaternionTrack( curve, context, out var rotationTrack ) )
						continue;

					boneCurves.RotationMode = rotationMode;
					boneCurves.Rotation = rotationTrack;
					break;

				case "s":
					if ( !TryResolveCurveMode( curve, modeOverrides, keyName, context, out var scaleMode ) ||
						!TryCreateVector3Track( curve, context, out var scaleTrack ) )
						continue;

					boneCurves.ScaleMode = scaleMode;
					boneCurves.Scale = scaleTrack;
					hasScaleKeys = true;
					break;

				case "sx":
				case "sy":
				case "sz":
					if ( !TryResolveCurveMode( curve, modeOverrides, keyName, context, out scaleMode ) ||
						!TryCreateFloatTrack( curve, context, out axisTrack ) )
						continue;

					boneCurves.ScaleMode = scaleMode;
					if ( keyName == "sx" ) boneCurves.ScaleX = axisTrack;
					if ( keyName == "sy" ) boneCurves.ScaleY = axisTrack;
					if ( keyName == "sz" ) boneCurves.ScaleZ = axisTrack;
					hasScaleKeys = true;
					break;

				default:
					context.Warn( $"Skipping unsupported CAST curve key \"{keyName}\" on bone \"{curve.NodeName}\"." );
					break;
			}
		}

		var animatedBones = curvesByBone.Values.Where( x => x.HasAnyCurves ).ToArray();
		if ( animatedBones.Length == 0 )
		{
			context.Warn( $"CAST animation \"{animationName}\" did not contain any usable bone curves." );
			return false;
		}

		var maxFrame = animatedBones.Max( x => x.MaxFrame );
		if ( maxFrame < 0 )
		{
			context.Warn( $"CAST animation \"{animationName}\" did not contain any usable keyframes." );
			return false;
		}

		var bindPose = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray();
		var frames = new CastAnimationFrameData[maxFrame + 1];

		for ( var frameIndex = 0; frameIndex <= maxFrame; frameIndex++ )
		{
			var frameTransforms = (Transform[])bindPose.Clone();

			foreach ( var curves in animatedBones )
			{
				var bindTransform = bindPose[curves.BoneIndex];
				var transform = bindTransform;

				if ( curves.PositionMode.HasValue && TryResolveAnimatedVector( curves.Position, curves.PositionX, curves.PositionY, curves.PositionZ, frameIndex, bindTransform.Position, out var sampledPosition ) )
				{
					transform.Position = ApplyVectorMode( curves.PositionMode.Value, bindTransform.Position, sampledPosition );
				}

				if ( curves.RotationMode.HasValue && curves.Rotation is not null && curves.Rotation.TrySample( frameIndex, out var sampledRotation ) )
				{
					var rotation = ApplyQuaternionMode( curves.RotationMode.Value, bindTransform.Rotation, sampledRotation );
					transform.Rotation = rotation;
				}

				if ( curves.ScaleMode.HasValue && TryResolveAnimatedVector( curves.Scale, curves.ScaleX, curves.ScaleY, curves.ScaleZ, frameIndex, bindTransform.Scale, out var sampledScale ) )
				{
					transform.Scale = ApplyScaleMode( curves.ScaleMode.Value, bindTransform.Scale, sampledScale );
				}

				frameTransforms[curves.BoneIndex] = transform;
			}

			frames[frameIndex] = new CastAnimationFrameData { BoneTransforms = frameTransforms };
		}

		var frameRate = animationNode.Framerate;
		if ( frameRate <= 0.0f || float.IsNaN( frameRate ) || float.IsInfinity( frameRate ) )
			frameRate = 30.0f;

		animationData = new CastAnimationData
		{
			Name = animationName,
			SourcePath = sourcePath ?? string.Empty,
			FrameRate = frameRate,
			Looping = animationNode.Looping,
			HasScaleKeys = hasScaleKeys,
			Events = CreateCastAnimationEventData( animationNode, context ),
			Frames = frames
		};

		return true;
	}

	static IReadOnlyList<CastAnimationEventData> CreateCastAnimationEventData( AnimationNode animationNode, CastImportContext context )
	{
		var events = new HashSet<CastAnimationEventData>();

		foreach ( var notificationTrack in animationNode.EnumerateNotificationTracks() )
		{
			var eventName = notificationTrack.Name?.Trim();
			if ( string.IsNullOrWhiteSpace( eventName ) )
			{
				context.Warn( "Skipping CAST notification track with an empty name." );
				continue;
			}

			try
			{
				foreach ( var rawFrame in notificationTrack.EnumerateKeyFrames() )
				{
					if ( double.IsNaN( rawFrame ) || double.IsInfinity( rawFrame ) || rawFrame < 0.0 )
					{
						context.Warn( $"Skipping invalid CAST notification keyframe for event \"{eventName}\"." );
						continue;
					}

					events.Add( new CastAnimationEventData( eventName, (int)rawFrame ) );
				}
			}
			catch ( Exception ex )
			{
				context.Warn( $"Skipping CAST notification track \"{eventName}\": {ex.Message}" );
			}
		}

		return events
			.OrderBy( x => x.Frame )
			.ThenBy( x => x.Name, StringComparer.Ordinal )
			.ToArray();
	}

	static bool TryApplyCastRootMotionOptions(
		CastSkeletonData skeletonData,
		IReadOnlyList<CastAnimationData> animations,
		CastAnimatedModelImportOptions options,
		CastImportContext context )
	{
		if ( options is null || options.AdvancedDataMode == CastAdvancedDataMode.BasicOnly || options.RootMotionMode == CastRootMotionMode.None )
			return true;

		if ( skeletonData is null || animations is null || animations.Count == 0 )
			return true;

		if ( !TryResolveCastRootMotionBone( skeletonData, options, out var boneIndex, out var boneName, out var failureReason ) )
		{
			context.Warn( failureReason );
			return options.AdvancedDataMode != CastAdvancedDataMode.StrictAdvanced;
		}

		foreach ( var animation in animations )
		{
			if ( animation is not null && animation.Frames.Length > 0 )
				animation.RootMotion = new CastRootMotionData( boneName, boneIndex );
		}

		return true;
	}

	static bool TryResolveCastRootMotionBone(
		CastSkeletonData skeletonData,
		CastAnimatedModelImportOptions options,
		out int boneIndex,
		out string boneName,
		out string failureReason )
	{
		boneIndex = -1;
		boneName = string.Empty;
		failureReason = string.Empty;

		if ( options.RootMotionMode == CastRootMotionMode.Bone )
		{
			var requestedBoneName = options.RootMotionBoneName?.Trim() ?? string.Empty;
			if ( string.IsNullOrWhiteSpace( requestedBoneName ) )
			{
				failureReason = "Root motion bone mode was selected, but no CAST bone name was provided.";
				return false;
			}

			boneIndex = Array.FindIndex( skeletonData.Bones, x => string.Equals( x.Name, requestedBoneName, StringComparison.OrdinalIgnoreCase ) );
			if ( boneIndex < 0 )
			{
				failureReason = $"Root motion bone \"{requestedBoneName}\" was not found in the imported CAST skeleton.";
				return false;
			}

			boneName = skeletonData.Bones[boneIndex].Name;
			return true;
		}

		foreach ( var candidate in new[] { "root", "pelvis", "hips" } )
		{
			boneIndex = Array.FindIndex( skeletonData.Bones, x => string.Equals( x.Name, candidate, StringComparison.OrdinalIgnoreCase ) );
			if ( boneIndex >= 0 )
			{
				boneName = skeletonData.Bones[boneIndex].Name;
				return true;
			}
		}

		failureReason = "Root motion auto mode could not find a CAST bone named root, pelvis, or hips.";
		return false;
	}

	static bool TryWriteCastModel(
		CastSourceData sourceData,
		IReadOnlyList<CastAnimationData> animations,
		string targetAbsolutePath,
		CreateModelFromMeshDialog.CollisionMode collisionMode,
		CastAnimatedModelImportOptions options,
		CastImportContext context,
		IReadOnlyList<RigsetReferenceData> rigsets = null )
	{
		options ??= CastAnimatedModelImportOptions.BasicOnly;

		if ( !TrySelectCastModelSourceWriter( sourceData, animations, options, context, out var writer ) )
			return false;

		return writer.TryWrite( sourceData, animations, targetAbsolutePath, collisionMode, context, rigsets );
	}

	internal static bool TrySelectCastModelSourceWriter(
		CastSourceData sourceData,
		IReadOnlyList<CastAnimationData> animations,
		CastAnimatedModelImportOptions options,
		CastImportContext context,
		out ICastModelSourceWriter writer )
	{
		writer = new SmdCastModelSourceWriter();

		if ( options is null || options.AdvancedDataMode == CastAdvancedDataMode.BasicOnly )
			return true;

		if ( !HasCastAdvancedData( sourceData, animations ) )
			return true;

		var dmxWriter = new DmxCastModelSourceWriter();
		if ( dmxWriter.CanWriteAdvancedData( sourceData, animations, options, out _ ) )
		{
			writer = dmxWriter;
			return true;
		}

		dmxWriter.CanWriteAdvancedData( sourceData, animations, options, out var reason );
		WarnIfCastAdvancedDataIsNotPreserved( sourceData, animations, reason, context );

		if ( options.AdvancedDataMode == CastAdvancedDataMode.StrictAdvanced )
			return false;

		return true;
	}

	static bool TryWriteSmdCastModel(
		CastSourceData sourceData,
		IReadOnlyList<CastAnimationData> animations,
		string targetAbsolutePath,
		CreateModelFromMeshDialog.CollisionMode collisionMode,
		CastImportContext context,
		IReadOnlyList<RigsetReferenceData> rigsets = null )
	{
		try
		{
			var targetDirectory = Path.GetDirectoryName( targetAbsolutePath );
			if ( string.IsNullOrWhiteSpace( targetDirectory ) )
			{
				context.Warn( $"Failed to build CAST model \"{targetAbsolutePath}\": target directory is missing." );
				return false;
			}

			if ( sourceData.Skeleton is null || sourceData.Skeleton.Bones.Length == 0 )
			{
				context.Warn( $"Failed to build CAST model \"{targetAbsolutePath}\": CAST animated import requires a skeleton." );
				return false;
			}

			Directory.CreateDirectory( targetDirectory );

			var targetName = Path.GetFileNameWithoutExtension( targetAbsolutePath );
			var sidecarDirectory = Path.Combine( targetDirectory, $"{targetName}.castimport" );
			if ( !TryPrepareCastSidecarDirectory( targetDirectory, sidecarDirectory, context ) )
				return false;

			WarnIfCastScaleIsIgnored( sourceData, animations, context );

			var meshMaterialTokens = new string[sourceData.Meshes.Length];
			var generatedMaterials = CreateGeneratedCastMaterials( sourceData.Meshes, meshMaterialTokens );

			string placeholderMaterialToken = null;
			var needsPlaceholderMesh = sourceData.Meshes.Length == 0;
			if ( needsPlaceholderMesh )
			{
				placeholderMaterialToken = $"cast_material_{generatedMaterials.Count}.vmat";
				generatedMaterials.Add( new CastGeneratedMaterial( placeholderMaterialToken, CastFallbackMaterial ) );
				context.Warn( $"CAST model \"{context.SourcePath}\" did not contain any meshes; generating a tiny placeholder mesh so imported animations remain available." );

				if ( collisionMode != CreateModelFromMeshDialog.CollisionMode.None )
					context.Warn( $"Skipping collision generation for CAST model \"{context.SourcePath}\" because only a placeholder mesh could be generated." );
			}

			var baseSmdPath = Path.Combine( sidecarDirectory, $"{targetName}.smd" );
			File.WriteAllText( baseSmdPath, CreateBaseCastSmdText( sourceData, meshMaterialTokens, placeholderMaterialToken ), Utf8WithoutBom );

			if ( !TryGetAssetRelativePath( baseSmdPath, context, out var baseSmdRelativePath ) )
				return false;

			var exportedAnimations = new List<CastGeneratedAnimationFile>();
			if ( animations is not null )
			{
				for ( var i = 0; i < animations.Count; i++ )
				{
					var animation = animations[i];
					if ( animation is null || animation.Frames.Length == 0 )
						continue;

					var animationFileName = $"{i:D3}_{SanitizeCastFileName( animation.Name )}.smd";
					var animationAbsolutePath = Path.Combine( sidecarDirectory, animationFileName );
					File.WriteAllText( animationAbsolutePath, CreateAnimationCastSmdText( sourceData.Skeleton, animation ), Utf8WithoutBom );

					if ( !TryGetAssetRelativePath( animationAbsolutePath, context, out var animationRelativePath ) )
						return false;

					exportedAnimations.Add( new CastGeneratedAnimationFile( animation, animationRelativePath ) );
				}
			}

			if ( !TryAppendRigsetAnimations( sourceData.Skeleton, rigsets, exportedAnimations, context ) )
				return false;

			var includeCollision = collisionMode != CreateModelFromMeshDialog.CollisionMode.None && !needsPlaceholderMesh;
			File.WriteAllText(
				targetAbsolutePath,
				CreateCastModelDocText( sourceData, baseSmdRelativePath, generatedMaterials, exportedAnimations, collisionMode, includeCollision ),
				Utf8WithoutBom );
			return true;
		}
		catch ( Exception ex )
		{
			context.Warn( $"Failed to build CAST model \"{targetAbsolutePath}\": {ex.Message}" );
			return false;
		}
	}

	static bool TryPrepareCastSidecarDirectory( string targetDirectory, string sidecarDirectory, CastImportContext context )
	{
		var fullTargetDirectory = Path.GetFullPath( targetDirectory );
		var fullSidecarDirectory = Path.GetFullPath( sidecarDirectory );
		if ( !IsPathWithinDirectory( fullSidecarDirectory, fullTargetDirectory ) )
		{
			context.Warn( $"Refusing to clean generated CAST sidecar directory outside target folder: \"{sidecarDirectory}\"." );
			return false;
		}

		if ( Directory.Exists( fullSidecarDirectory ) )
			Directory.Delete( fullSidecarDirectory, true );

		Directory.CreateDirectory( fullSidecarDirectory );
		return true;
	}

	static List<CastGeneratedMaterial> CreateGeneratedCastMaterials( IReadOnlyList<CastMeshData> meshes, string[] meshMaterialTokens )
	{
		var generatedMaterials = new List<CastGeneratedMaterial>();
		var tokenByMaterial = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		for ( var i = 0; i < meshes.Count; i++ )
		{
			var materialName = string.IsNullOrWhiteSpace( meshes[i].MaterialName ) ? CastFallbackMaterial : meshes[i].MaterialName;
			if ( !tokenByMaterial.TryGetValue( materialName, out var token ) )
			{
				token = $"cast_material_{generatedMaterials.Count}.vmat";
				tokenByMaterial.Add( materialName, token );
				generatedMaterials.Add( new CastGeneratedMaterial( token, materialName ) );
			}

			meshMaterialTokens[i] = token;
		}

		return generatedMaterials;
	}

	static void WarnIfCastScaleIsIgnored( CastSourceData sourceData, IReadOnlyList<CastAnimationData> animations, CastImportContext context )
	{
		if ( sourceData.Skeleton.Bones.Any( x => !IsIdentityScale( x.LocalTransform.Scale ) ) )
			context.Warn( $"CAST skeleton \"{sourceData.Name}\" contains non-identity bone scale; the V1 SMD export path ignores scale." );

		if ( animations is null || sourceData.Skeleton is null )
			return;

		if ( animations.Any( x => x is not null && x.HasScaleKeys ) )
		{
			context.Warn( $"CAST animation data contains scale keys; the V1 SMD export path ignores scale." );
			return;
		}

		var bindPose = sourceData.Skeleton.Bones.Select( x => x.LocalTransform.Scale ).ToArray();
		foreach ( var animation in animations )
		{
			if ( animation is null )
				continue;

			foreach ( var frame in animation.Frames )
			{
				for ( var boneIndex = 0; boneIndex < frame.BoneTransforms.Length && boneIndex < bindPose.Length; boneIndex++ )
				{
					if ( !IsApproximatelyEqual( frame.BoneTransforms[boneIndex].Scale, bindPose[boneIndex] ) )
					{
						context.Warn( $"CAST animation \"{animation.Name}\" contains scale keys; the V1 SMD export path ignores scale." );
						goto Done;
					}
				}
			}
		}

	Done:
		return;
	}

	static bool HasCastAdvancedData( CastSourceData sourceData, IReadOnlyList<CastAnimationData> animations )
	{
		if ( sourceData.BlendShapes.Length > 0 || sourceData.IkHandles.Length > 0 || sourceData.Constraints.Length > 0 )
			return true;

		if ( animations is null )
			return false;

		return animations.Any( x => x is not null && (x.HasScaleKeys || x.Events.Count > 0 || x.RootMotion is not null) );
	}

	static void WarnIfCastAdvancedDataIsNotPreserved(
		CastSourceData sourceData,
		IReadOnlyList<CastAnimationData> animations,
		string reason,
		CastImportContext context )
	{
		context.Warn( $"CAST advanced data in \"{sourceData.Name}\" cannot be preserved yet: {reason}" );

		if ( animations is not null && animations.Any( x => x is not null && x.HasScaleKeys ) )
			context.Warn( "CAST animation scale curves are parsed but will fall back to SMD until advanced DMX animation import is validated." );

		if ( animations is not null && animations.Any( x => x is not null && x.Events.Count > 0 ) )
			context.Warn( "CAST notification events are parsed but will not be emitted until the ModelDoc event schema is fixture-confirmed." );

		if ( animations is not null && animations.Any( x => x is not null && x.RootMotion is not null ) )
			context.Warn( "CAST root motion selection is parsed but will not be emitted until the ModelDoc root-motion schema is fixture-confirmed." );

		if ( sourceData.BlendShapes.Length > 0 )
			context.Warn( "CAST blend shapes are parsed as transformed vertex deltas but will not be emitted until DMX deltaStates preservation is validated." );

		if ( sourceData.IkHandles.Length > 0 )
			context.Warn( "CAST IK handles are parsed by bone hash but will not be emitted until ModelDoc IKData output is fixture-confirmed." );

		if ( sourceData.Constraints.Length > 0 )
			context.Warn( "CAST constraints are parsed by bone hash and kept warning-only until a supported ModelDoc constraint node shape is fixture-confirmed." );
	}

	static string CreateBaseCastSmdText( CastSourceData sourceData, IReadOnlyList<string> meshMaterialTokens, string placeholderMaterialToken )
	{
		var builder = new StringBuilder();
		builder.AppendLine( "version 1" );
		AppendSmdNodesSection( builder, sourceData.Skeleton );
		AppendSmdSkeletonFrame( builder, sourceData.Skeleton, 0, sourceData.Skeleton.Bones.Select( x => x.LocalTransform ) );

		builder.AppendLine( "triangles" );
		for ( var meshIndex = 0; meshIndex < sourceData.Meshes.Length; meshIndex++ )
		{
			var meshData = sourceData.Meshes[meshIndex];
			var materialToken = meshMaterialTokens[meshIndex];
			foreach ( var face in meshData.Faces )
			{
				builder.AppendLine( materialToken );
				AppendSmdVertex( builder, sourceData.Skeleton, meshData, face.A );
				AppendSmdVertex( builder, sourceData.Skeleton, meshData, face.B );
				AppendSmdVertex( builder, sourceData.Skeleton, meshData, face.C );
			}
		}

		if ( sourceData.Meshes.Length == 0 && !string.IsNullOrWhiteSpace( placeholderMaterialToken ) )
		{
			builder.AppendLine( placeholderMaterialToken );
			AppendPlaceholderSmdVertex( builder, 0, new Vector3( 0.0f, 0.0f, 0.0f ), Vector3.Up, new Vector2( 0.0f, 0.0f ) );
			AppendPlaceholderSmdVertex( builder, 0, new Vector3( 0.01f, 0.0f, 0.0f ), Vector3.Up, new Vector2( 1.0f, 0.0f ) );
			AppendPlaceholderSmdVertex( builder, 0, new Vector3( 0.0f, 0.01f, 0.0f ), Vector3.Up, new Vector2( 0.0f, 1.0f ) );
		}

		builder.AppendLine( "end" );
		return builder.ToString();
	}

	static string CreateAnimationCastSmdText( CastSkeletonData skeletonData, CastAnimationData animation )
	{
		var builder = new StringBuilder();
		builder.AppendLine( "version 1" );
		AppendSmdNodesSection( builder, skeletonData );
		builder.AppendLine( "skeleton" );
		for ( var frameIndex = 0; frameIndex < animation.Frames.Length; frameIndex++ )
		{
			builder.AppendLine( $"time {frameIndex}" );
			for ( var boneIndex = 0; boneIndex < skeletonData.Bones.Length; boneIndex++ )
			{
				var transform = animation.Frames[frameIndex].BoneTransforms[boneIndex];
				var rotation = ToSmdRotation( transform.Rotation );
				builder
					.Append( boneIndex )
					.Append( ' ' )
					.Append( FormatFloat( transform.Position.x ) )
					.Append( ' ' )
					.Append( FormatFloat( transform.Position.y ) )
					.Append( ' ' )
					.Append( FormatFloat( transform.Position.z ) )
					.Append( ' ' )
					.Append( FormatFloat( rotation.x ) )
					.Append( ' ' )
					.Append( FormatFloat( rotation.y ) )
					.Append( ' ' )
					.Append( FormatFloat( rotation.z ) )
					.AppendLine();
			}
		}

		builder.AppendLine( "end" );
		return builder.ToString();
	}

	static string CreateCastModelDocText(
		CastSourceData sourceData,
		string baseSmdRelativePath,
		IReadOnlyList<CastGeneratedMaterial> generatedMaterials,
		IReadOnlyList<CastGeneratedAnimationFile> animations,
		CreateModelFromMeshDialog.CollisionMode collisionMode,
		bool includeCollision )
	{
		var builder = new StringBuilder();
		builder.AppendLine( "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->" );
		builder.AppendLine( "{" );
		builder.AppendLine( "\trootNode =" );
		builder.AppendLine( "\t{" );
		builder.AppendLine( "\t\t_class = \"RootNode\"" );
		builder.AppendLine( "\t\tchildren =" );
		builder.AppendLine( "\t\t[" );

		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( "\t\t\t\t_class = \"MaterialGroupList\"" );
		builder.AppendLine( "\t\t\t\tchildren =" );
		builder.AppendLine( "\t\t\t\t[" );
		builder.AppendLine( "\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"" );
		builder.AppendLine( "\t\t\t\t\t\tremaps =" );
		builder.AppendLine( "\t\t\t\t\t\t[" );
		foreach ( var material in generatedMaterials )
		{
			builder.AppendLine( "\t\t\t\t\t\t\t{" );
			builder.AppendLine( $"\t\t\t\t\t\t\t\tfrom = \"{EscapeKv3String( material.SourceName )}\"" );
			builder.AppendLine( $"\t\t\t\t\t\t\t\tto = \"{EscapeKv3String( material.TargetMaterialName )}\"" );
			builder.AppendLine( "\t\t\t\t\t\t\t}," );
		}
		builder.AppendLine( "\t\t\t\t\t\t]" );
		builder.AppendLine( "\t\t\t\t\t\tuse_global_default = false" );
		builder.AppendLine( "\t\t\t\t\t\tglobal_default_material = \"\"" );
		builder.AppendLine( "\t\t\t\t\t}," );
		builder.AppendLine( "\t\t\t\t]" );
		builder.AppendLine( "\t\t\t}," );

		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( "\t\t\t\t_class = \"RenderMeshList\"" );
		builder.AppendLine( "\t\t\t\tchildren =" );
		builder.AppendLine( "\t\t\t\t[" );
		builder.AppendLine( "\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t_class = \"RenderMeshFile\"" );
		builder.AppendLine( $"\t\t\t\t\t\tname = \"{EscapeKv3String( sourceData.Name )}\"" );
		builder.AppendLine( $"\t\t\t\t\t\tfilename = \"{EscapeKv3String( baseSmdRelativePath )}\"" );
		builder.AppendLine( "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]" );
		builder.AppendLine( "\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]" );
		builder.AppendLine( "\t\t\t\t\t\timport_scale = 1.0" );
		builder.AppendLine( "\t\t\t\t\t\talign_origin_x_type = \"None\"" );
		builder.AppendLine( "\t\t\t\t\t\talign_origin_y_type = \"None\"" );
		builder.AppendLine( "\t\t\t\t\t\talign_origin_z_type = \"None\"" );
		builder.AppendLine( "\t\t\t\t\t\tparent_bone = \"\"" );
		builder.AppendLine( "\t\t\t\t\t\timport_filter =" );
		builder.AppendLine( "\t\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t\texclude_by_default = false" );
		builder.AppendLine( "\t\t\t\t\t\t\texception_list = [ ]" );
		builder.AppendLine( "\t\t\t\t\t\t}" );
		builder.AppendLine( "\t\t\t\t\t}," );
		builder.AppendLine( "\t\t\t\t]" );
		builder.AppendLine( "\t\t\t}," );

		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( "\t\t\t\t_class = \"AnimationList\"" );
		builder.AppendLine( "\t\t\t\tchildren =" );
		builder.AppendLine( "\t\t\t\t[" );
		builder.AppendLine( "\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t_class = \"AnimBindPose\"" );
		builder.AppendLine( "\t\t\t\t\t\tname = \"bindPose\"" );
		builder.AppendLine( "\t\t\t\t\t\tactivity_name = \"\"" );
		builder.AppendLine( "\t\t\t\t\t\tactivity_weight = 1" );
		builder.AppendLine( "\t\t\t\t\t\tweight_list_name = \"\"" );
		builder.AppendLine( "\t\t\t\t\t\tfade_in_time = 0.2" );
		builder.AppendLine( "\t\t\t\t\t\tfade_out_time = 0.2" );
		builder.AppendLine( "\t\t\t\t\t\tlooping = false" );
		builder.AppendLine( "\t\t\t\t\t\tdelta = false" );
		builder.AppendLine( "\t\t\t\t\t\tworldSpace = false" );
		builder.AppendLine( "\t\t\t\t\t\thidden = false" );
		builder.AppendLine( "\t\t\t\t\t\tanim_markup_ordered = false" );
		builder.AppendLine( "\t\t\t\t\t\tdisable_compression = false" );
		builder.AppendLine( "\t\t\t\t\t\tdisable_interpolation = false" );
		builder.AppendLine( "\t\t\t\t\t\tenable_scale = false" );
		builder.AppendLine( "\t\t\t\t\t\tframe_count = 1" );
		builder.AppendLine( "\t\t\t\t\t\tframe_rate = 30" );
		builder.AppendLine( "\t\t\t\t\t}," );

		foreach ( var animation in animations )
		{
			AppendCastAnimFileModelDocText( builder, animation, "\t\t\t\t\t" );
		}

		builder.AppendLine( "\t\t\t\t]" );
		builder.AppendLine( "\t\t\t\tdefault_root_bone_name = \"\"" );
		builder.AppendLine( "\t\t\t}," );

		if ( includeCollision )
		{
			builder.AppendLine( "\t\t\t{" );
			builder.AppendLine( "\t\t\t\t_class = \"PhysicsShapeList\"" );
			builder.AppendLine( "\t\t\t\tchildren =" );
			builder.AppendLine( "\t\t\t\t[" );
			builder.AppendLine( "\t\t\t\t\t{" );
			builder.AppendLine( $"\t\t\t\t\t\t_class = \"{GetCollisionNodeClassName( collisionMode )}\"" );
			if ( collisionMode == CreateModelFromMeshDialog.CollisionMode.Hull )
			{
				builder.AppendLine( "\t\t\t\t\t\tparent_bone = \"\"" );
				builder.AppendLine( "\t\t\t\t\t\tsurface_prop = \"default\"" );
				builder.AppendLine( "\t\t\t\t\t\tcollision_prop = \"default\"" );
				builder.AppendLine( "\t\t\t\t\t\tfaceMergeAngle = 20.0" );
				builder.AppendLine( "\t\t\t\t\t\tmaxHullVertices = 32" );
			}
			builder.AppendLine( "\t\t\t\t\t}," );
			builder.AppendLine( "\t\t\t\t]" );
			builder.AppendLine( "\t\t\t}," );
		}

		builder.AppendLine( "\t\t]" );
		builder.AppendLine( "\t\tmodel_archetype = \"\"" );
		builder.AppendLine( "\t\tprimary_associated_entity = \"\"" );
		builder.AppendLine( "\t\tanim_graph_name = \"\"" );
		builder.AppendLine( "\t}" );
		builder.AppendLine( "}" );
		return builder.ToString();
	}

	static void AppendCastAnimFileModelDocText( StringBuilder builder, CastGeneratedAnimationFile animation, string indent )
	{
		builder.AppendLine( $"{indent}{{" );
		builder.AppendLine( $"{indent}\t_class = \"AnimFile\"" );
		builder.AppendLine( $"{indent}\tname = \"{EscapeKv3String( animation.Animation.Name )}\"" );
		builder.AppendLine( $"{indent}\tactivity_name = \"\"" );
		builder.AppendLine( $"{indent}\tactivity_weight = 1" );
		builder.AppendLine( $"{indent}\tweight_list_name = \"\"" );
		builder.AppendLine( $"{indent}\tfade_in_time = 0.2" );
		builder.AppendLine( $"{indent}\tfade_out_time = 0.2" );
		builder.AppendLine( $"{indent}\tlooping = {FormatBool( animation.Animation.Looping )}" );
		builder.AppendLine( $"{indent}\tdelta = false" );
		builder.AppendLine( $"{indent}\tworldSpace = false" );
		builder.AppendLine( $"{indent}\thidden = false" );
		builder.AppendLine( $"{indent}\tanim_markup_ordered = false" );
		builder.AppendLine( $"{indent}\tdisable_compression = false" );
		builder.AppendLine( $"{indent}\tdisable_interpolation = false" );
		builder.AppendLine( $"{indent}\tenable_scale = false" );
		builder.AppendLine( $"{indent}\tsource_filename = \"{EscapeKv3String( animation.RelativePath )}\"" );
		builder.AppendLine( $"{indent}\tstart_frame = -1" );
		builder.AppendLine( $"{indent}\tend_frame = -1" );
		builder.AppendLine( $"{indent}\tframerate = {FormatFloat( animation.Animation.FrameRate )}" );
		builder.AppendLine( $"{indent}\ttake = 0" );
		builder.AppendLine( $"{indent}\treverse = false" );
		builder.AppendLine( $"{indent}}}," );
	}

	internal static string CreateCastModelDocTextForTests( CastSourceData sourceData, IReadOnlyList<CastAnimationData> animations )
	{
		var generatedAnimations = animations?
			.Where( x => x is not null )
			.Select( x => new CastGeneratedAnimationFile( x, $"{SanitizeCastFileName( x.Name )}.smd" ) )
			.ToArray() ?? [];

		return CreateCastModelDocText(
			sourceData,
			"model.smd",
			[],
			generatedAnimations,
			CreateModelFromMeshDialog.CollisionMode.None,
			includeCollision: false );
	}

	internal static string CreateAnimationCastSmdTextForTests( CastSkeletonData skeletonData, CastAnimationData animation )
	{
		return CreateAnimationCastSmdText( skeletonData, animation );
	}

	static void AppendSmdNodesSection( StringBuilder builder, CastSkeletonData skeletonData )
	{
		builder.AppendLine( "nodes" );
		for ( var i = 0; i < skeletonData.Bones.Length; i++ )
		{
			builder
				.Append( i )
				.Append( " \"" )
				.Append( EscapeSmdString( skeletonData.Bones[i].Name ) )
				.Append( "\" " )
				.Append( skeletonData.Bones[i].ParentIndex )
				.AppendLine();
		}

		builder.AppendLine( "end" );
	}

	static void AppendSmdSkeletonFrame( StringBuilder builder, CastSkeletonData skeletonData, int frameIndex, IEnumerable<Transform> boneTransforms )
	{
		builder.AppendLine( "skeleton" );
		builder.AppendLine( $"time {frameIndex}" );
		var transforms = boneTransforms.ToArray();
		for ( var boneIndex = 0; boneIndex < skeletonData.Bones.Length && boneIndex < transforms.Length; boneIndex++ )
		{
			var transform = transforms[boneIndex];
			var rotation = ToSmdRotation( transform.Rotation );
			builder
				.Append( boneIndex )
				.Append( ' ' )
				.Append( FormatFloat( transform.Position.x ) )
				.Append( ' ' )
				.Append( FormatFloat( transform.Position.y ) )
				.Append( ' ' )
				.Append( FormatFloat( transform.Position.z ) )
				.Append( ' ' )
				.Append( FormatFloat( rotation.x ) )
				.Append( ' ' )
				.Append( FormatFloat( rotation.y ) )
				.Append( ' ' )
				.Append( FormatFloat( rotation.z ) )
				.AppendLine();
		}

		builder.AppendLine( "end" );
	}

	static void AppendSmdVertex( StringBuilder builder, CastSkeletonData skeletonData, CastMeshData meshData, int vertexIndex )
	{
		var position = meshData.Positions[vertexIndex];
		var normal = meshData.Normals[vertexIndex];
		var texCoord = meshData.TexCoords[vertexIndex];
		var weights = EnumerateSmdVertexWeights( skeletonData, meshData, vertexIndex ).ToArray();
		var parentBone = weights.Length > 0 ? weights[0].BoneIndex : 0;

		builder
			.Append( parentBone )
			.Append( ' ' )
			.Append( FormatFloat( position.x ) )
			.Append( ' ' )
			.Append( FormatFloat( position.y ) )
			.Append( ' ' )
			.Append( FormatFloat( position.z ) )
			.Append( ' ' )
			.Append( FormatFloat( normal.x ) )
			.Append( ' ' )
			.Append( FormatFloat( normal.y ) )
			.Append( ' ' )
			.Append( FormatFloat( normal.z ) )
			.Append( ' ' )
			.Append( FormatFloat( texCoord.x ) )
			.Append( ' ' )
			.Append( FormatFloat( texCoord.y ) );

		if ( weights.Length > 0 )
		{
			builder.Append( ' ' ).Append( weights.Length );
			foreach ( var weight in weights )
				builder.Append( ' ' ).Append( weight.BoneIndex ).Append( ' ' ).Append( FormatFloat( weight.Weight ) );
		}

		builder.AppendLine();
	}

	static void AppendPlaceholderSmdVertex( StringBuilder builder, int boneIndex, Vector3 position, Vector3 normal, Vector2 texCoord )
	{
		builder
			.Append( boneIndex )
			.Append( ' ' )
			.Append( FormatFloat( position.x ) )
			.Append( ' ' )
			.Append( FormatFloat( position.y ) )
			.Append( ' ' )
			.Append( FormatFloat( position.z ) )
			.Append( ' ' )
			.Append( FormatFloat( normal.x ) )
			.Append( ' ' )
			.Append( FormatFloat( normal.y ) )
			.Append( ' ' )
			.Append( FormatFloat( normal.z ) )
			.Append( ' ' )
			.Append( FormatFloat( texCoord.x ) )
			.Append( ' ' )
			.Append( FormatFloat( texCoord.y ) )
			.Append( " 1 " )
			.Append( boneIndex )
			.Append( " 1" )
			.AppendLine();
	}

	static IEnumerable<CastVertexWeight> EnumerateSmdVertexWeights( CastSkeletonData skeletonData, CastMeshData meshData, int vertexIndex )
	{
		if ( !meshData.UsesSkinning || meshData.BlendIndices.Length <= vertexIndex || meshData.BlendWeights.Length <= vertexIndex )
		{
			yield return new CastVertexWeight( 0, 1.0f );
			yield break;
		}

		var indices = meshData.BlendIndices[vertexIndex];
		var weights = meshData.BlendWeights[vertexIndex];
		var influences = new List<CastVertexWeight>( 4 );
		AddSmdVertexWeight( influences, skeletonData.Bones.Length, indices.r, weights.r );
		AddSmdVertexWeight( influences, skeletonData.Bones.Length, indices.g, weights.g );
		AddSmdVertexWeight( influences, skeletonData.Bones.Length, indices.b, weights.b );
		AddSmdVertexWeight( influences, skeletonData.Bones.Length, indices.a, weights.a );

		if ( influences.Count == 0 )
		{
			yield return new CastVertexWeight( 0, 1.0f );
			yield break;
		}

		var totalWeight = influences.Sum( x => x.Weight );
		if ( totalWeight <= 0.0f )
		{
			yield return new CastVertexWeight( 0, 1.0f );
			yield break;
		}

		for ( var i = 0; i < influences.Count; i++ )
			yield return new CastVertexWeight( influences[i].BoneIndex, influences[i].Weight / totalWeight );
	}

	static void AddSmdVertexWeight( List<CastVertexWeight> influences, int boneCount, byte boneIndex, byte weightByte )
	{
		if ( weightByte == 0 || boneIndex >= boneCount )
			return;

		influences.Add( new CastVertexWeight( boneIndex, weightByte / 255.0f ) );
	}

	internal static bool TryGetAssetRelativePath( string absolutePath, CastImportContext context, out string relativePath )
	{
		var assetsPath = Project.Current?.GetAssetsPath();
		if ( string.IsNullOrWhiteSpace( assetsPath ) )
		{
			context.Warn( "Failed to resolve generated CAST asset path because the current project assets path is unavailable." );
			relativePath = null;
			return false;
		}

		var fullAssetsPath = Path.GetFullPath( assetsPath );
		var fullAssetPath = Path.GetFullPath( absolutePath );
		if ( !IsPathWithinDirectory( fullAssetPath, fullAssetsPath ) )
		{
			context.Warn( $"Generated CAST artifact \"{absolutePath}\" is outside the current assets path \"{assetsPath}\"." );
			relativePath = null;
			return false;
		}

		relativePath = Path.GetRelativePath( fullAssetsPath, fullAssetPath ).Replace( '\\', '/' );
		return true;
	}

	static bool IsPathWithinDirectory( string candidatePath, string directoryPath )
	{
		var fullDirectoryPath = Path.GetFullPath( directoryPath ).TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
		var fullCandidatePath = Path.GetFullPath( candidatePath ).TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );

		if ( string.Equals( fullCandidatePath, fullDirectoryPath, StringComparison.OrdinalIgnoreCase ) )
			return true;

		return fullCandidatePath.StartsWith( fullDirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) ||
			fullCandidatePath.StartsWith( fullDirectoryPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
	}

	static string GetCollisionNodeClassName( CreateModelFromMeshDialog.CollisionMode collisionMode )
	{
		return collisionMode switch
		{
			CreateModelFromMeshDialog.CollisionMode.Mesh => "PhysicsMeshFromRender",
			_ => "PhysicsHullFromRender"
		};
	}

	static Vector3 ToSmdRotation( Rotation rotation )
	{
		var angles = rotation.Angles();
		return new Vector3(
			MathX.DegreeToRadian( angles.roll ),
			MathX.DegreeToRadian( angles.pitch ),
			MathX.DegreeToRadian( angles.yaw ) );
	}

	static bool IsIdentityScale( Vector3 scale )
	{
		return IsApproximatelyEqual( scale, Vector3.One );
	}

	static bool IsApproximatelyEqual( Vector3 left, Vector3 right )
	{
		const float epsilon = 0.001f;
		return MathF.Abs( left.x - right.x ) <= epsilon &&
			MathF.Abs( left.y - right.y ) <= epsilon &&
			MathF.Abs( left.z - right.z ) <= epsilon;
	}

	static string EscapeKv3String( string value )
	{
		return (value ?? string.Empty).Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
	}

	static string EscapeSmdString( string value )
	{
		return (value ?? string.Empty).Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
	}

	internal static string GetUniqueCastAnimationName( string baseName, HashSet<string> usedNames )
	{
		var sanitized = SanitizeCastFileName( baseName );
		var candidate = sanitized;
		var suffix = 1;

		while ( !usedNames.Add( candidate ) )
		{
			candidate = $"{sanitized}_{suffix:D3}";
			suffix++;
		}

		return candidate;
	}

	static string SanitizeCastFileName( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return "animation";

		var builder = new StringBuilder( value.Length );
		foreach ( var ch in value )
		{
			if ( Array.IndexOf( Path.GetInvalidFileNameChars(), ch ) >= 0 || char.IsWhiteSpace( ch ) )
				builder.Append( '_' );
			else
				builder.Append( ch );
		}

		return builder.Length == 0 ? "animation" : builder.ToString();
	}

	static string FormatFloat( float value )
	{
		if ( MathF.Abs( value ) < 0.0000005f )
			value = 0.0f;

		return value.ToString( "0.######", CultureInfo.InvariantCulture );
	}

	static string FormatBool( bool value )
	{
		return value ? "true" : "false";
	}

	readonly record struct CastGeneratedMaterial( string SourceName, string TargetMaterialName );

	internal readonly record struct CastGeneratedAnimationFile( CastAnimationData Animation, string RelativePath );

	readonly record struct CastVertexWeight( int BoneIndex, float Weight );

	internal interface ICastModelSourceWriter
	{
		string Name { get; }
		bool CanWriteAdvancedData( CastSourceData sourceData, IReadOnlyList<CastAnimationData> animations, CastAnimatedModelImportOptions options, out string reason );
		bool TryWrite(
			CastSourceData sourceData,
			IReadOnlyList<CastAnimationData> animations,
			string targetAbsolutePath,
			CreateModelFromMeshDialog.CollisionMode collisionMode,
			CastImportContext context,
			IReadOnlyList<RigsetReferenceData> rigsets );
	}

	sealed class SmdCastModelSourceWriter : ICastModelSourceWriter
	{
		public string Name => "SMD";

		public bool CanWriteAdvancedData( CastSourceData sourceData, IReadOnlyList<CastAnimationData> animations, CastAnimatedModelImportOptions options, out string reason )
		{
			reason = "SMD is the V1 basic export path and does not preserve advanced animation data.";
			return false;
		}

		public bool TryWrite(
			CastSourceData sourceData,
			IReadOnlyList<CastAnimationData> animations,
			string targetAbsolutePath,
			CreateModelFromMeshDialog.CollisionMode collisionMode,
			CastImportContext context,
			IReadOnlyList<RigsetReferenceData> rigsets )
		{
			return TryWriteSmdCastModel( sourceData, animations, targetAbsolutePath, collisionMode, context, rigsets );
		}
	}

	sealed class DmxCastModelSourceWriter : ICastModelSourceWriter
	{
		public string Name => "DMX";

		public bool CanWriteAdvancedData( CastSourceData sourceData, IReadOnlyList<CastAnimationData> animations, CastAnimatedModelImportOptions options, out string reason )
		{
			reason = "the DMX writer is gated until generated DMX and ModelDoc schema fixtures compile and preserve the requested data.";
			return false;
		}

		public bool TryWrite(
			CastSourceData sourceData,
			IReadOnlyList<CastAnimationData> animations,
			string targetAbsolutePath,
			CreateModelFromMeshDialog.CollisionMode collisionMode,
			CastImportContext context,
			IReadOnlyList<RigsetReferenceData> rigsets )
		{
			CanWriteAdvancedData( sourceData, animations, CastAnimatedModelImportOptions.BasicOnly, out var reason );
			context.Warn( $"Skipping DMX CAST export: {reason}" );
			return false;
		}
	}

	static IEnumerable<AnimationNode> EnumerateCastAnimations( Cast.NET.Cast cast )
	{
		return EnumerateCastNodes<AnimationNode>( cast );
	}

	static IEnumerable<TNode> EnumerateCastNodes<TNode>( Cast.NET.Cast cast ) where TNode : CastNode
	{
		foreach ( var root in cast.RootNodes )
		{
			foreach ( var node in EnumerateCastNodes<TNode>( root ) )
				yield return node;
		}
	}

	static IEnumerable<TNode> EnumerateCastNodes<TNode>( CastNode node ) where TNode : CastNode
	{
		if ( node is TNode match )
			yield return match;

		foreach ( var child in node.Children )
		{
			foreach ( var nested in EnumerateCastNodes<TNode>( child ) )
				yield return nested;
		}
	}

	static bool TryResolveCurveMode(
		CurveNode curve,
		IReadOnlyDictionary<string, CurveModeOverrideNode> modeOverrides,
		string keyName,
		CastImportContext context,
		out CastAnimationMode mode )
	{
		var effectiveMode = curve.Mode?.Trim();

		if ( modeOverrides.TryGetValue( curve.NodeName, out var modeOverride ) && ShouldApplyModeOverride( keyName, modeOverride ) && !string.IsNullOrWhiteSpace( modeOverride.Mode ) )
			effectiveMode = modeOverride.Mode.Trim();

		switch ( effectiveMode?.ToLowerInvariant() )
		{
			case "absolute":
				mode = CastAnimationMode.Absolute;
				return true;

			case "relative":
				mode = CastAnimationMode.Relative;
				return true;

			case "additive":
				mode = CastAnimationMode.Additive;
				return true;

			default:
				context.Warn( $"Skipping CAST curve with unsupported mode \"{curve.Mode}\" on bone \"{curve.NodeName}\"." );
				mode = default;
				return false;
		}
	}

	static bool ShouldApplyModeOverride( string keyName, CurveModeOverrideNode modeOverride )
	{
		return keyName switch
		{
			"lp" or "p" or "tx" or "ty" or "tz" => modeOverride.OverrideTranslationCurves,
			"lr" or "r" or "rq" => modeOverride.OverrideRotationCurves,
			"s" or "sx" or "sy" or "sz" => modeOverride.OverrideScaleCurves,
			_ => false
		};
	}

	static bool TryCreateFloatTrack( CurveNode curve, CastImportContext context, out CastCurveTrack<float> track )
	{
		return TryCreateCurveTrack(
			curve,
			context,
			values => values is Cast.NET.CastArrayProperty<float> floatValues ? floatValues.Values : null,
			value => !float.IsNaN( value ) && !float.IsInfinity( value ),
			out track );
	}

	static bool TryCreateVector3Track( CurveNode curve, CastImportContext context, out CastCurveTrack<NumericsVector3> track )
	{
		return TryCreateCurveTrack(
			curve,
			context,
			values => values is Cast.NET.CastArrayProperty<NumericsVector3> vectorValues ? vectorValues.Values : null,
			IsFinite,
			out track );
	}

	static bool TryCreateQuaternionTrack( CurveNode curve, CastImportContext context, out CastCurveTrack<NumericsQuaternion> track )
	{
		track = null;

		if ( !TryReadCurveFrames( curve, context, out var frames ) )
			return false;

		if ( curve.KeyValueBuffer is not Cast.NET.CastArrayProperty<NumericsVector4> quaternionValues )
		{
			context.Warn( $"Skipping CAST quaternion curve on bone \"{curve.NodeName}\" because value buffer type \"{curve.KeyValueBuffer.GetType().Name}\" is unsupported." );
			return false;
		}

		if ( quaternionValues.ValueCount != frames.Length )
		{
			context.Warn( $"Skipping CAST quaternion curve on bone \"{curve.NodeName}\" because key/value counts do not match." );
			return false;
		}

		var pairs = new List<KeyValuePair<int, NumericsQuaternion>>( frames.Length );
		for ( var i = 0; i < frames.Length; i++ )
		{
			var value = quaternionValues.Values[i];
			var quaternion = new NumericsQuaternion( value.X, value.Y, value.Z, value.W );
			if ( !TryNormalizeCastQuaternion( quaternion, out quaternion ) )
			{
				context.Warn( $"Skipping invalid quaternion key on CAST bone \"{curve.NodeName}\"." );
				continue;
			}

			pairs.Add( new KeyValuePair<int, NumericsQuaternion>( frames[i], quaternion ) );
		}

		if ( pairs.Count == 0 )
			return false;

		track = CreateCurveTrack( pairs );
		return true;
	}

	static bool TryCreateCurveTrack<T>(
		CurveNode curve,
		CastImportContext context,
		Func<CastProperty, IReadOnlyList<T>> valuesAccessor,
		Func<T, bool> validator,
		out CastCurveTrack<T> track ) where T : unmanaged
	{
		track = null;

		if ( !TryReadCurveFrames( curve, context, out var frames ) )
			return false;

		var values = valuesAccessor( curve.KeyValueBuffer );
		if ( values is null )
		{
			context.Warn( $"Skipping CAST curve on bone \"{curve.NodeName}\" because value buffer type \"{curve.KeyValueBuffer.GetType().Name}\" is unsupported." );
			return false;
		}

		if ( values.Count != frames.Length )
		{
			context.Warn( $"Skipping CAST curve on bone \"{curve.NodeName}\" because key/value counts do not match." );
			return false;
		}

		var pairs = new List<KeyValuePair<int, T>>( frames.Length );
		for ( var i = 0; i < frames.Length; i++ )
		{
			if ( !validator( values[i] ) )
			{
				context.Warn( $"Skipping invalid CAST key value on bone \"{curve.NodeName}\"." );
				continue;
			}

			pairs.Add( new KeyValuePair<int, T>( frames[i], values[i] ) );
		}

		if ( pairs.Count == 0 )
			return false;

		track = CreateCurveTrack( pairs );
		return true;
	}

	static CastCurveTrack<T> CreateCurveTrack<T>( List<KeyValuePair<int, T>> pairs ) where T : unmanaged
	{
		pairs.Sort( static ( a, b ) => a.Key.CompareTo( b.Key ) );

		var uniqueFrames = new List<int>( pairs.Count );
		var uniqueValues = new List<T>( pairs.Count );

		foreach ( var (frame, value) in pairs )
		{
			if ( uniqueFrames.Count > 0 && uniqueFrames[^1] == frame )
			{
				uniqueValues[^1] = value;
			}
			else
			{
				uniqueFrames.Add( frame );
				uniqueValues.Add( value );
			}
		}

		return new CastCurveTrack<T>( uniqueFrames.ToArray(), uniqueValues.ToArray() );
	}

	static bool TryReadCurveFrames( CurveNode curve, CastImportContext context, out int[] frames )
	{
		frames = null;

		try
		{
			var rawFrames = curve.EnumerateKeyFrames().ToArray();
			if ( rawFrames.Length == 0 )
				return false;

			frames = new int[rawFrames.Length];
			for ( var i = 0; i < rawFrames.Length; i++ )
			{
				var rawFrame = rawFrames[i];
				if ( double.IsNaN( rawFrame ) || double.IsInfinity( rawFrame ) || rawFrame < 0.0 )
				{
					context.Warn( $"Skipping invalid CAST keyframe on bone \"{curve.NodeName}\"." );
					return false;
				}

				frames[i] = (int)rawFrame;
			}

			return true;
		}
		catch ( Exception ex )
		{
			context.Warn( $"Skipping CAST curve on bone \"{curve.NodeName}\": {ex.Message}" );
			return false;
		}
	}

	static bool TryResolveAnimatedVector(
		CastCurveTrack<NumericsVector3> vectorTrack,
		CastCurveTrack<float> xTrack,
		CastCurveTrack<float> yTrack,
		CastCurveTrack<float> zTrack,
		int frameIndex,
		Vector3 bindValue,
		out Vector3 value )
	{
		if ( vectorTrack is not null && vectorTrack.TrySample( frameIndex, out var vectorValue ) )
		{
			value = ToSandboxVector3( vectorValue );
			return true;
		}

		value = bindValue;
		var sampled = false;

		if ( xTrack is not null && xTrack.TrySample( frameIndex, out var x ) )
		{
			value.x = x;
			sampled = true;
		}

		if ( yTrack is not null && yTrack.TrySample( frameIndex, out var y ) )
		{
			value.y = y;
			sampled = true;
		}

		if ( zTrack is not null && zTrack.TrySample( frameIndex, out var z ) )
		{
			value.z = z;
			sampled = true;
		}

		return sampled;
	}

	static Vector3 ApplyVectorMode( CastAnimationMode mode, Vector3 bindValue, Vector3 sampledValue )
	{
		return mode switch
		{
			CastAnimationMode.Absolute => sampledValue,
			CastAnimationMode.Relative => bindValue + sampledValue,
			CastAnimationMode.Additive => bindValue + sampledValue,
			_ => bindValue
		};
	}

	static Vector3 ApplyScaleMode( CastAnimationMode mode, Vector3 bindValue, Vector3 sampledValue )
	{
		return mode switch
		{
			CastAnimationMode.Absolute => sampledValue,
			CastAnimationMode.Relative => bindValue * sampledValue,
			CastAnimationMode.Additive => bindValue * sampledValue,
			_ => bindValue
		};
	}

	static Rotation ApplyQuaternionMode( CastAnimationMode mode, Rotation bindValue, NumericsQuaternion sampledValue )
	{
		var rotation = mode switch
		{
			CastAnimationMode.Absolute => (Rotation)sampledValue,
			CastAnimationMode.Relative => bindValue * (Rotation)sampledValue,
			CastAnimationMode.Additive => bindValue * (Rotation)sampledValue,
			_ => bindValue
		};

		return rotation.Normal;
	}

	static bool IsFinite( NumericsVector3 value )
	{
		return !float.IsNaN( value.X ) && !float.IsInfinity( value.X ) &&
			!float.IsNaN( value.Y ) && !float.IsInfinity( value.Y ) &&
			!float.IsNaN( value.Z ) && !float.IsInfinity( value.Z );
	}

	static bool IsIdentityCastModelTransform( CastModelTransform transform )
	{
		return transform.Position == NumericsVector3.Zero &&
			((transform.Scale == NumericsVector3.Zero && transform.Rotation == default) ||
			(transform.Scale == NumericsVector3.One && transform.Rotation == NumericsQuaternion.Identity));
	}
}
