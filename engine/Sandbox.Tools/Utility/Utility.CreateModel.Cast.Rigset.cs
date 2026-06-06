using Sandbox;
using Sandbox.Internal;
using Sandbox.Resources;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Editor;

public static partial class EditorUtility
{
	// Architecture note: rigsets are managed animation indexes, not native animation
	// libraries. The compiler owns deterministic animation sidecars under
	// "<name>.rigsetimport", and compatible models merge those sidecars into their
	// ModelDoc AnimationList entries after skeleton validation.
	internal sealed class RigsetReferenceData
	{
		public string Path { get; init; } = string.Empty;
		public AnimationRigset Rigset { get; init; }
	}

	public static Asset CreateAnimationRigsetFromCastFiles(
		Asset baseCastFile,
		IReadOnlyList<Asset> animationCastFiles,
		string animationFolderAbsolutePath,
		string targetAbsolutePath,
		CastAnimatedModelImportOptions options,
		bool compileImmediately = true )
	{
		if ( baseCastFile is null )
			return null;

		if ( string.IsNullOrWhiteSpace( targetAbsolutePath ) )
			return null;

		var sourcePath = baseCastFile.AbsolutePath;
		if ( string.IsNullOrWhiteSpace( sourcePath ) || !File.Exists( sourcePath ) )
			return null;

		var context = new CastImportContext( sourcePath );
		try
		{
			if ( !TryCreateAnimationRigsetData(
				sourcePath,
				GetCastAssetPaths( animationCastFiles ),
				animationFolderAbsolutePath,
				targetAbsolutePath,
				options,
				context,
				out var rigset ) )
			{
				return null;
			}

			var targetDirectory = Path.GetDirectoryName( targetAbsolutePath );
			if ( !string.IsNullOrWhiteSpace( targetDirectory ) )
				Directory.CreateDirectory( targetDirectory );

			var asset = AssetSystem.CreateResource( "rigset", targetAbsolutePath );
			if ( asset is null )
				return null;

			if ( !asset.SaveToDisk( rigset ) )
				return null;

			if ( compileImmediately )
				asset.Compile( true );

			return asset;
		}
		finally
		{
			context.FlushWarnings();
		}
	}

	internal static IReadOnlyList<RigsetReferenceData> LoadRigsetReferences( IReadOnlyList<Asset> rigsetFiles, CastImportContext context )
	{
		if ( rigsetFiles is null || rigsetFiles.Count == 0 )
			return [];

		var rigsets = new List<RigsetReferenceData>();
		var usedPaths = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var rigsetAsset in rigsetFiles )
		{
			if ( rigsetAsset is null || string.IsNullOrWhiteSpace( rigsetAsset.AbsolutePath ) )
				continue;

			if ( !string.Equals( rigsetAsset.AssetType.FileExtension, "rigset", StringComparison.OrdinalIgnoreCase ) )
				continue;

			var rigsetPath = Path.GetFullPath( rigsetAsset.AbsolutePath );
			if ( !usedPaths.Add( rigsetPath ) )
			{
				context.Warn( $"Skipping duplicate rigset reference \"{rigsetAsset.AbsolutePath}\"." );
				continue;
			}

			if ( !rigsetAsset.Compile( true ) || rigsetAsset.IsCompileFailed )
			{
				context.Warn( $"Failed to compile rigset \"{rigsetAsset.AbsolutePath}\" before model generation." );
				continue;
			}

			if ( !TryLoadAnimationRigset( rigsetAsset.AbsolutePath, context, out var rigset ) )
				continue;

			rigsets.Add( new RigsetReferenceData
			{
				Path = rigsetPath,
				Rigset = rigset
			} );
		}

		return rigsets;
	}

	internal static bool TryCreateAnimationRigsetData(
		string baseCastPath,
		IReadOnlyList<string> animationCastPaths,
		string animationFolderAbsolutePath,
		string targetAbsolutePath,
		CastAnimatedModelImportOptions options,
		CastImportContext context,
		out AnimationRigset rigset )
	{
		rigset = null;
		options ??= CastAnimatedModelImportOptions.BasicOnly;

		var resolvedAnimationPaths = ResolveRigsetAnimationCastPaths( baseCastPath, animationCastPaths, animationFolderAbsolutePath );
		if ( !TryCollectCastAnimatedImportData( baseCastPath, resolvedAnimationPaths, options, context, out var importData ) )
			return false;

		if ( importData.SourceData.Skeleton is null || importData.SourceData.Skeleton.Bones.Length == 0 )
		{
			context.Warn( $"CAST rigset \"{targetAbsolutePath}\" requires a base CAST skeleton." );
			return false;
		}

		if ( importData.Animations.Count == 0 )
		{
			context.Warn( $"CAST rigset \"{targetAbsolutePath}\" did not find any usable animation clips." );
			return false;
		}

		var sidecarDirectory = GetRigsetSidecarDirectory( targetAbsolutePath );
		if ( string.IsNullOrWhiteSpace( sidecarDirectory ) )
		{
			context.Warn( $"Failed to build CAST rigset \"{targetAbsolutePath}\": target directory is missing." );
			return false;
		}

		if ( !TrySelectRigsetSidecarFormat( importData.SourceData, importData.Animations, options, context, out var sidecarFormat ) )
			return false;

		var exportedAnimations = CreateExpectedRigsetAnimationFiles( importData.SourceData.Skeleton, importData.Animations, sidecarDirectory, context, sidecarFormat );
		if ( exportedAnimations is null )
			return false;

		return TryCreateAnimationRigsetData(
			baseCastPath,
			resolvedAnimationPaths,
			animationFolderAbsolutePath,
			targetAbsolutePath,
			options,
			importData.SourceData.Skeleton,
			importData.Animations,
			exportedAnimations,
			context,
			out rigset );
	}

	internal static bool TryCreateAnimationRigsetData(
		string baseCastPath,
		IReadOnlyList<string> animationCastPaths,
		string animationFolderAbsolutePath,
		string targetAbsolutePath,
		CastAnimatedModelImportOptions options,
		CastSkeletonData skeletonData,
		IReadOnlyList<CastAnimationData> animations,
		IReadOnlyList<CastGeneratedAnimationFile> exportedAnimations,
		CastImportContext context,
		out AnimationRigset rigset )
	{
		rigset = null;

		if ( !TryGetAssetRelativePath( baseCastPath, context, out var baseCastRelativePath ) )
			return false;

		var animationFolderRelativePath = string.Empty;
		if ( !string.IsNullOrWhiteSpace( animationFolderAbsolutePath ) && Directory.Exists( animationFolderAbsolutePath ) )
		{
			if ( !TryGetAssetRelativePath( animationFolderAbsolutePath, context, out animationFolderRelativePath ) )
				return false;
		}

		var animationRelativePaths = new List<string>();
		foreach ( var animationPath in animationCastPaths ?? [] )
		{
			if ( !TryGetAssetRelativePath( animationPath, context, out var animationRelativePath ) )
				return false;

			animationRelativePaths.Add( animationRelativePath );
		}

		var rigsetAnimations = new List<AnimationRigsetAnimation>();
		foreach ( var exportedAnimation in exportedAnimations ?? [] )
		{
			if ( exportedAnimation.Animation is null )
				continue;

			var animation = exportedAnimation.Animation;
			var sourceCastPath = string.IsNullOrWhiteSpace( animation.SourcePath ) ? baseCastPath : animation.SourcePath;
			if ( !TryGetAssetRelativePath( sourceCastPath, context, out var sourceCastRelativePath ) )
				return false;

			rigsetAnimations.Add( new AnimationRigsetAnimation
			{
				Name = animation.Name,
				SourceCast = sourceCastRelativePath,
				FrameRate = animation.FrameRate,
				Looping = animation.Looping,
				HasScaleKeys = animation.HasScaleKeys,
				FrameCount = animation.Frames.Length,
				SidecarPath = exportedAnimation.RelativePath,
				Events = animation.Events
					.Select( x => new AnimationRigsetAnimationEvent { Name = x.Name, Frame = x.Frame } )
					.ToList(),
				RootMotion = animation.RootMotion is { } rootMotion
					? new AnimationRigsetRootMotion { BoneName = rootMotion.BoneName, BoneIndex = rootMotion.BoneIndex }
					: null
			} );
		}

		rigset = new AnimationRigset
		{
			Version = 1,
			BaseCast = baseCastRelativePath,
			AnimationFolder = animationFolderRelativePath,
			AnimationFiles = animationRelativePaths,
			AdvancedDataMode = (options ?? CastAnimatedModelImportOptions.BasicOnly).AdvancedDataMode.ToString(),
			RootMotionMode = (options ?? CastAnimatedModelImportOptions.BasicOnly).RootMotionMode.ToString(),
			RootMotionBoneName = (options ?? CastAnimatedModelImportOptions.BasicOnly).RootMotionBoneName ?? string.Empty,
			SidecarFormat = GetAnimationRigsetSidecarFormat( exportedAnimations ),
			SkeletonSignature = CreateAnimationRigsetSkeletonSignature( skeletonData ),
			Animations = rigsetAnimations,
			Warnings = context.Warnings.ToList()
		};

		return true;
	}

	internal static bool TryWriteRigsetSidecarFiles(
		string rigsetAbsolutePath,
		CastSkeletonData skeletonData,
		IReadOnlyList<CastAnimationData> animations,
		CastImportContext context,
		out List<CastGeneratedAnimationFile> exportedAnimations,
		CastAnimatedModelImportOptions options = null )
	{
		exportedAnimations = null;
		options ??= CastAnimatedModelImportOptions.BasicOnly;

		var targetDirectory = Path.GetDirectoryName( rigsetAbsolutePath );
		if ( string.IsNullOrWhiteSpace( targetDirectory ) )
		{
			context.Warn( $"Failed to build CAST rigset \"{rigsetAbsolutePath}\": target directory is missing." );
			return false;
		}

		if ( skeletonData is null || skeletonData.Bones.Length == 0 )
		{
			context.Warn( $"Failed to build CAST rigset \"{rigsetAbsolutePath}\": CAST rigset import requires a skeleton." );
			return false;
		}

		var sidecarDirectory = GetRigsetSidecarDirectory( rigsetAbsolutePath );
		if ( !TryPrepareCastSidecarDirectory( targetDirectory, sidecarDirectory, context ) )
			return false;

		var sourceData = new CastSourceData
		{
			Name = Path.GetFileNameWithoutExtension( rigsetAbsolutePath ),
			Skeleton = skeletonData
		};

		if ( !TrySelectRigsetSidecarFormat( sourceData, animations, options, context, out var sidecarFormat ) )
			return false;

		if ( string.Equals( sidecarFormat, "smd", StringComparison.OrdinalIgnoreCase ) )
			WarnIfCastScaleIsIgnored( sourceData, animations, context );

		exportedAnimations = CreateExpectedRigsetAnimationFiles( skeletonData, animations, sidecarDirectory, context, sidecarFormat );
		if ( exportedAnimations is null )
			return false;

		foreach ( var exportedAnimation in exportedAnimations )
		{
			var animationAbsolutePath = Path.Combine( sidecarDirectory, CreateRigsetAnimationSidecarFileName( exportedAnimation.Animation, sidecarFormat ) );
			var sidecarText = string.Equals( sidecarFormat, "dmx", StringComparison.OrdinalIgnoreCase )
				? CreateAnimationCastDmxText( skeletonData, exportedAnimation.Animation )
				: CreateAnimationCastSmdText( skeletonData, exportedAnimation.Animation );
			File.WriteAllText( animationAbsolutePath, sidecarText, Utf8WithoutBom );
		}

		return true;
	}

	internal static bool TryAppendRigsetAnimations(
		CastSkeletonData modelSkeleton,
		IReadOnlyList<RigsetReferenceData> rigsets,
		List<CastGeneratedAnimationFile> generatedAnimations,
		CastImportContext context,
		HashSet<string> usedAnimationNames = null )
	{
		if ( rigsets is null || rigsets.Count == 0 )
			return true;

		usedAnimationNames ??= generatedAnimations
			.Select( x => x.Animation?.Name )
			.Where( x => !string.IsNullOrWhiteSpace( x ) )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		var usedRigsetPaths = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var rigsetReference in rigsets )
		{
			if ( rigsetReference?.Rigset is null )
				continue;

			if ( !string.IsNullOrWhiteSpace( rigsetReference.Path ) && !usedRigsetPaths.Add( rigsetReference.Path ) )
			{
				context.Warn( $"Skipping duplicate rigset reference \"{rigsetReference.Path}\"." );
				continue;
			}

			if ( !IsAnimationRigsetSkeletonCompatible( rigsetReference.Rigset.SkeletonSignature, modelSkeleton, out var reason ) )
			{
				context.Warn( $"Rigset \"{rigsetReference.Path}\" is not compatible with the model skeleton: {reason} Animation references will still be added." );
			}

			foreach ( var rigsetAnimation in rigsetReference.Rigset.Animations ?? [] )
			{
				if ( rigsetAnimation is null )
					continue;

				if ( string.IsNullOrWhiteSpace( rigsetAnimation.SidecarPath ) )
				{
					context.Warn( $"Skipping rigset animation \"{rigsetAnimation.Name}\" because it has no sidecar path." );
					continue;
				}

				if ( !string.IsNullOrWhiteSpace( rigsetReference.Path ) &&
					TryResolveRigsetAssetPath( rigsetAnimation.SidecarPath, rigsetReference.Path, context, out var sidecarAbsolutePath ) &&
					!File.Exists( sidecarAbsolutePath ) )
				{
					context.Warn( $"Skipping rigset animation \"{rigsetAnimation.Name}\" because sidecar file \"{rigsetAnimation.SidecarPath}\" does not exist." );
					continue;
				}

				var uniqueName = GetUniqueCastAnimationName( rigsetAnimation.Name, usedAnimationNames );
				if ( !string.Equals( uniqueName, rigsetAnimation.Name, StringComparison.Ordinal ) )
					context.Warn( $"Renaming duplicate rigset animation \"{rigsetAnimation.Name}\" to \"{uniqueName}\"." );

				generatedAnimations.Add( new CastGeneratedAnimationFile(
					new CastAnimationData
					{
						Name = uniqueName,
						FrameRate = rigsetAnimation.FrameRate,
						Looping = rigsetAnimation.Looping,
						HasScaleKeys = rigsetAnimation.HasScaleKeys
					},
					rigsetAnimation.SidecarPath ) );
			}
		}

		return true;
	}

	internal static bool TryAppendRigsetAnimationsToModelDoc(
		string modelDocText,
		IReadOnlyList<RigsetReferenceData> rigsets,
		CastSkeletonData modelSkeleton,
		CastImportContext context,
		out string mergedModelDocText )
	{
		mergedModelDocText = modelDocText;
		if ( string.IsNullOrWhiteSpace( modelDocText ) )
			return false;

		var usedNames = CollectModelDocAnimationNames( modelDocText );
		var generatedAnimations = new List<CastGeneratedAnimationFile>();
		if ( !TryAppendRigsetAnimations( modelSkeleton, rigsets, generatedAnimations, context, usedNames ) )
			return false;

		if ( generatedAnimations.Count == 0 )
			return true;

		var marker = "\t\t\t\t]\r\n\t\t\t\tdefault_root_bone_name";
		var insertIndex = modelDocText.IndexOf( marker, StringComparison.Ordinal );
		if ( insertIndex < 0 )
		{
			marker = "\t\t\t\t]\n\t\t\t\tdefault_root_bone_name";
			insertIndex = modelDocText.IndexOf( marker, StringComparison.Ordinal );
		}

		if ( insertIndex < 0 )
		{
			context.Warn( "Failed to append rigset animations because the generated ModelDoc AnimationList was not found." );
			return false;
		}

		var builder = new StringBuilder();
		foreach ( var animation in generatedAnimations )
			AppendCastAnimFileModelDocText( builder, animation, "\t\t\t\t\t" );

		mergedModelDocText = modelDocText.Insert( insertIndex, builder.ToString() );
		return true;
	}

	static HashSet<string> CollectModelDocAnimationNames( string modelDocText )
	{
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var animationListIndex = modelDocText.IndexOf( "_class = \"AnimationList\"", StringComparison.Ordinal );
		if ( animationListIndex < 0 )
			return names;

		var endIndex = modelDocText.IndexOf( "default_root_bone_name", animationListIndex, StringComparison.Ordinal );
		if ( endIndex < 0 )
			endIndex = modelDocText.Length;

		var animationListText = modelDocText.Substring( animationListIndex, endIndex - animationListIndex );
		foreach ( var rawLine in animationListText.Split( '\n' ) )
		{
			var line = rawLine.Trim();
			if ( !line.StartsWith( "name = \"", StringComparison.Ordinal ) )
				continue;

			var nameStart = "name = \"".Length;
			var nameEnd = line.IndexOf( '"', nameStart );
			if ( nameEnd <= nameStart )
				continue;

			names.Add( line.Substring( nameStart, nameEnd - nameStart ) );
		}

		return names;
	}

	static List<CastGeneratedAnimationFile> CreateExpectedRigsetAnimationFiles(
		CastSkeletonData skeletonData,
		IReadOnlyList<CastAnimationData> animations,
		string sidecarDirectory,
		CastImportContext context,
		string sidecarFormat = "dmx" )
	{
		var exportedAnimations = new List<CastGeneratedAnimationFile>();
		foreach ( var animation in animations ?? [] )
		{
			if ( animation is null || animation.Frames.Length == 0 )
				continue;

			var animationAbsolutePath = Path.Combine( sidecarDirectory, CreateRigsetAnimationSidecarFileName( animation, sidecarFormat ) );
			if ( !TryGetAssetRelativePath( animationAbsolutePath, context, out var animationRelativePath ) )
				return null;

			exportedAnimations.Add( new CastGeneratedAnimationFile( animation, animationRelativePath ) );
		}

		return exportedAnimations;
	}

	internal static bool TrySelectRigsetSidecarFormat(
		CastSourceData sourceData,
		IReadOnlyList<CastAnimationData> animations,
		CastAnimatedModelImportOptions options,
		CastImportContext context,
		out string sidecarFormat )
	{
		options ??= CastAnimatedModelImportOptions.BasicOnly;

		if ( options.AdvancedDataMode == CastAdvancedDataMode.BasicOnly )
		{
			sidecarFormat = "smd";
			return true;
		}

		sidecarFormat = "dmx";

		var dmxWriter = new DmxCastModelSourceWriter();
		if ( dmxWriter.CanWriteAdvancedData( sourceData, animations, options, out _ ) )
			return true;

		dmxWriter.CanWriteAdvancedData( sourceData, animations, options, out var reason );
		WarnIfCastAdvancedDataIsNotPreserved( sourceData, animations, reason, context );
		return false;
	}

	static string GetAnimationRigsetSidecarFormat( IReadOnlyList<CastGeneratedAnimationFile> exportedAnimations )
	{
		if ( exportedAnimations is null || exportedAnimations.Count == 0 )
			return "dmx";

		return string.Equals( Path.GetExtension( exportedAnimations[0].RelativePath ), ".smd", StringComparison.OrdinalIgnoreCase )
			? "smd"
			: "dmx";
	}

	internal static string CreateRigsetAnimationSidecarFileName( CastAnimationData animation, string sidecarFormat = "dmx" )
	{
		var animationName = animation?.Name ?? "animation";
		var extension = string.Equals( sidecarFormat, "smd", StringComparison.OrdinalIgnoreCase ) ? "smd" : "dmx";
		return $"{SanitizeCastFileName( animationName )}.{extension}";
	}

	internal static AnimationRigsetSkeletonSignature CreateAnimationRigsetSkeletonSignature( CastSkeletonData skeletonData )
	{
		var signature = new AnimationRigsetSkeletonSignature();
		if ( skeletonData is null )
			return signature;

		foreach ( var bone in skeletonData.Bones )
		{
			signature.Bones.Add( new AnimationRigsetBone
			{
				Name = bone.Name,
				ParentIndex = bone.ParentIndex,
				SourceHash = FormatCastSourceHash( bone.SourceHash )
			} );
		}

		signature.Hash = ComputeAnimationRigsetSkeletonHash( signature.Bones );
		return signature;
	}

	internal static bool IsAnimationRigsetSkeletonCompatible(
		AnimationRigsetSkeletonSignature rigsetSkeleton,
		CastSkeletonData modelSkeleton,
		out string reason )
	{
		reason = string.Empty;

		if ( rigsetSkeleton is null || rigsetSkeleton.Bones.Count == 0 )
		{
			reason = "Rigset has no skeleton signature.";
			return false;
		}

		if ( modelSkeleton is null || modelSkeleton.Bones.Length == 0 )
		{
			reason = "Model has no skeleton.";
			return false;
		}

		if ( rigsetSkeleton.Bones.Count != modelSkeleton.Bones.Length )
		{
			reason = $"Skeleton bone count mismatch: rigset has {rigsetSkeleton.Bones.Count}, model has {modelSkeleton.Bones.Length}.";
			return false;
		}

		for ( var i = 0; i < modelSkeleton.Bones.Length; i++ )
		{
			var rigsetBone = rigsetSkeleton.Bones[i];
			var modelBone = modelSkeleton.Bones[i];
			if ( !string.Equals( rigsetBone.Name, modelBone.Name, StringComparison.Ordinal ) )
			{
				reason = $"Skeleton bone {i} name mismatch: rigset has \"{rigsetBone.Name}\", model has \"{modelBone.Name}\".";
				return false;
			}

			if ( rigsetBone.ParentIndex != modelBone.ParentIndex )
			{
				reason = $"Skeleton bone \"{rigsetBone.Name}\" parent mismatch: rigset has {rigsetBone.ParentIndex}, model has {modelBone.ParentIndex}.";
				return false;
			}

		}

		return true;
	}

	static string GetRigsetSidecarDirectory( string rigsetAbsolutePath )
	{
		var targetDirectory = Path.GetDirectoryName( rigsetAbsolutePath );
		if ( string.IsNullOrWhiteSpace( targetDirectory ) )
			return string.Empty;

		return Path.Combine( targetDirectory, $"{Path.GetFileNameWithoutExtension( rigsetAbsolutePath )}.rigsetimport" );
	}

	internal static List<string> ResolveRigsetAnimationCastPaths( string baseCastPath, IReadOnlyList<string> animationCastPaths, string animationFolderAbsolutePath )
	{
		var paths = new List<string>();
		var usedPaths = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		void AddPath( string path )
		{
			if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
				return;

			if ( string.Equals( path, baseCastPath, StringComparison.OrdinalIgnoreCase ) )
				return;

			var fullPath = Path.GetFullPath( path );
			if ( usedPaths.Add( fullPath ) )
				paths.Add( fullPath );
		}

		foreach ( var path in animationCastPaths ?? [] )
			AddPath( path );

		if ( !string.IsNullOrWhiteSpace( animationFolderAbsolutePath ) && Directory.Exists( animationFolderAbsolutePath ) )
		{
			foreach ( var filePath in Directory.GetFiles( animationFolderAbsolutePath, "*.cast", SearchOption.TopDirectoryOnly ).OrderBy( x => x, StringComparer.OrdinalIgnoreCase ) )
			{
				if ( !InspectCastFile( filePath ).HasAnimation )
					continue;

				AddPath( filePath );
			}
		}

		return paths;
	}

	static string FormatCastSourceHash( ulong sourceHash )
	{
		return sourceHash == 0 ? string.Empty : $"0x{sourceHash:X16}";
	}

	static string ComputeAnimationRigsetSkeletonHash( IReadOnlyList<AnimationRigsetBone> bones )
	{
		var builder = new StringBuilder();
		foreach ( var bone in bones )
		{
			builder
				.Append( bone.Name )
				.Append( '\0' )
				.Append( bone.ParentIndex )
				.Append( '\0' )
				.Append( bone.SourceHash )
				.Append( '\n' );
		}

		var bytes = SHA256.HashData( Encoding.UTF8.GetBytes( builder.ToString() ) );
		return Convert.ToHexString( bytes ).ToLowerInvariant();
	}

	internal static bool TryLoadAnimationRigset( string rigsetAbsolutePath, CastImportContext context, out AnimationRigset rigset )
	{
		rigset = null;

		if ( string.IsNullOrWhiteSpace( rigsetAbsolutePath ) || !File.Exists( rigsetAbsolutePath ) )
		{
			context.Warn( $"Rigset file \"{rigsetAbsolutePath}\" does not exist." );
			return false;
		}

		try
		{
			rigset = new AnimationRigset();
			rigset.LoadFromJson( File.ReadAllText( rigsetAbsolutePath ) );
			return true;
		}
		catch ( Exception ex )
		{
			context.Warn( $"Failed to load rigset \"{rigsetAbsolutePath}\": {ex.Message}" );
			rigset = null;
			return false;
		}
	}

	internal static bool TryResolveRigsetAssetPath( string path, string containingAbsolutePath, CastImportContext context, out string absolutePath )
	{
		absolutePath = null;

		if ( string.IsNullOrWhiteSpace( path ) )
			return false;

		if ( Path.IsPathRooted( path ) )
		{
			absolutePath = Path.GetFullPath( path );
			return true;
		}

		var assetsPath = Project.Current?.GetAssetsPath();
		if ( !string.IsNullOrWhiteSpace( assetsPath ) )
		{
			absolutePath = Path.GetFullPath( Path.Combine( assetsPath, path.Replace( '/', Path.DirectorySeparatorChar ) ) );
			return true;
		}

		var containingDirectory = Path.GetDirectoryName( containingAbsolutePath );
		if ( !string.IsNullOrWhiteSpace( containingDirectory ) )
		{
			absolutePath = Path.GetFullPath( Path.Combine( containingDirectory, path.Replace( '/', Path.DirectorySeparatorChar ) ) );
			return true;
		}

		context.Warn( $"Failed to resolve rigset path \"{path}\"." );
		return false;
	}

	internal static CastAnimatedModelImportOptions CreateRigsetImportOptions( AnimationRigset rigset )
	{
		var options = new CastAnimatedModelImportOptions();

		if ( Enum.TryParse<CastAdvancedDataMode>( rigset?.AdvancedDataMode, ignoreCase: true, out var advancedDataMode ) )
			options.AdvancedDataMode = advancedDataMode;

		if ( Enum.TryParse<CastRootMotionMode>( rigset?.RootMotionMode, ignoreCase: true, out var rootMotionMode ) )
			options.RootMotionMode = rootMotionMode;

		options.RootMotionBoneName = rigset?.RootMotionBoneName ?? string.Empty;
		return options;
	}
}

[Expose]
[ResourceIdentity( "rigset" )]
public sealed class AnimationRigsetCompiler : ResourceCompiler
{
	protected override Task<bool> Compile()
	{
		var context = new EditorUtility.CastImportContext( Context.AbsolutePath );
		try
		{
			if ( !EditorUtility.TryLoadAnimationRigset( Context.AbsolutePath, context, out var sourceRigset ) )
				return Task.FromResult( false );

			if ( !EditorUtility.TryResolveRigsetAssetPath( sourceRigset.BaseCast, Context.AbsolutePath, context, out var baseCastPath ) || !File.Exists( baseCastPath ) )
			{
				context.Warn( $"Rigset \"{Context.RelativePath}\" has no readable base CAST file." );
				return Task.FromResult( false );
			}

			var animationFolderPath = string.Empty;
			if ( !string.IsNullOrWhiteSpace( sourceRigset.AnimationFolder ) )
				EditorUtility.TryResolveRigsetAssetPath( sourceRigset.AnimationFolder, Context.AbsolutePath, context, out animationFolderPath );

			var animationPaths = new List<string>();
			foreach ( var animationFile in sourceRigset.AnimationFiles ?? [] )
			{
				if ( EditorUtility.TryResolveRigsetAssetPath( animationFile, Context.AbsolutePath, context, out var animationPath ) )
					animationPaths.Add( animationPath );
			}

			animationPaths = EditorUtility.ResolveRigsetAnimationCastPaths( baseCastPath, animationPaths, animationFolderPath );

			Context.AddCompileReference( sourceRigset.BaseCast );
			foreach ( var animationPath in animationPaths )
			{
				if ( EditorUtility.TryGetAssetRelativePath( animationPath, context, out var animationRelativePath ) )
					Context.AddCompileReference( animationRelativePath );
			}

			var options = EditorUtility.CreateRigsetImportOptions( sourceRigset );
			if ( !EditorUtility.TryCollectCastAnimatedImportData( baseCastPath, animationPaths, options, context, out var importData ) )
				return Task.FromResult( false );

			if ( !EditorUtility.TryWriteRigsetSidecarFiles( Context.AbsolutePath, importData.SourceData.Skeleton, importData.Animations, context, out var exportedAnimations, options ) )
				return Task.FromResult( false );

			if ( !EditorUtility.TryCreateAnimationRigsetData(
				baseCastPath,
				animationPaths,
				animationFolderPath,
				Context.AbsolutePath,
				options,
				importData.SourceData.Skeleton,
				importData.Animations,
				exportedAnimations,
				context,
				out var compiledRigset ) )
			{
				return Task.FromResult( false );
			}

			Context.ResourceVersion = compiledRigset.Version;
			Context.Data.Write( compiledRigset.Serialize().ToJsonString( GlobalToolsNamespace.EditorJsonOptions ) );
			return Task.FromResult( true );
		}
		finally
		{
			context.FlushWarnings();
		}
	}
}
