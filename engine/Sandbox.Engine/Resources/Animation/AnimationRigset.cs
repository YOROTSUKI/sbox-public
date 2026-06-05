namespace Sandbox;

/// <summary>
/// A reusable index of animation clips generated from CAST animation sources.
/// </summary>
[AssetType( Name = "Animation Rigset", Extension = "rigset", Category = "Animation", Flags = AssetTypeFlags.NoEmbedding )]
public sealed class AnimationRigset : GameResource
{
	public int Version { get; set; } = 1;

	/// <summary>
	/// CAST file used as the source skeleton for compatibility checks.
	/// </summary>
	public string BaseCast { get; set; } = string.Empty;

	/// <summary>
	/// Optional folder used to discover animation CAST files.
	/// </summary>
	public string AnimationFolder { get; set; } = string.Empty;

	/// <summary>
	/// Explicit animation CAST files included in this rigset.
	/// </summary>
	public List<string> AnimationFiles { get; set; } = [];

	public string AdvancedDataMode { get; set; } = "BasicOnly";
	public string RootMotionMode { get; set; } = "Auto";
	public string RootMotionBoneName { get; set; } = string.Empty;

	public AnimationRigsetSkeletonSignature SkeletonSignature { get; set; } = new();

	public List<AnimationRigsetAnimation> Animations { get; set; } = [];

	public List<string> Warnings { get; set; } = [];

	protected override Bitmap CreateAssetTypeIcon( int width, int height )
	{
		return CreateSimpleAssetTypeIcon( "animation", width, height, "#4b7a78", "#e9fffb" );
	}
}

public sealed class AnimationRigsetSkeletonSignature
{
	public string Hash { get; set; } = string.Empty;
	public List<AnimationRigsetBone> Bones { get; set; } = [];
}

public sealed class AnimationRigsetBone
{
	public string Name { get; set; } = string.Empty;
	public int ParentIndex { get; set; } = -1;
	public string SourceHash { get; set; } = string.Empty;
}

public sealed class AnimationRigsetAnimation
{
	public string Name { get; set; } = string.Empty;
	public string SourceCast { get; set; } = string.Empty;
	public float FrameRate { get; set; } = 30.0f;
	public bool Looping { get; set; }
	public bool HasScaleKeys { get; set; }
	public int FrameCount { get; set; }
	public string SidecarPath { get; set; } = string.Empty;
	public List<AnimationRigsetAnimationEvent> Events { get; set; } = [];
	public AnimationRigsetRootMotion RootMotion { get; set; }
}

public sealed class AnimationRigsetAnimationEvent
{
	public string Name { get; set; } = string.Empty;
	public int Frame { get; set; }
}

public sealed class AnimationRigsetRootMotion
{
	public string BoneName { get; set; } = string.Empty;
	public int BoneIndex { get; set; } = -1;
}
