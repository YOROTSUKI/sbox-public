using Cast.NET.Nodes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NumQuat = System.Numerics.Quaternion;
using NumVec3 = System.Numerics.Vector3;

[TestClass]
public class CastAnimationRigsetTests
{
	[TestMethod]
	public void SkeletonSignatureIsStableForSameSkeleton()
	{
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var left ) );
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var right ) );

		var leftSignature = Editor.EditorUtility.CreateAnimationRigsetSkeletonSignature( left );
		var rightSignature = Editor.EditorUtility.CreateAnimationRigsetSkeletonSignature( right );

		Assert.AreEqual( leftSignature.Hash, rightSignature.Hash );
		Assert.AreEqual( 2, leftSignature.Bones.Count );
	}

	[TestMethod]
	public void SkeletonSignatureChangesForDifferentBoneLayout()
	{
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var left ) );
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton( childName: "other_child" ), out var right ) );

		var leftSignature = Editor.EditorUtility.CreateAnimationRigsetSkeletonSignature( left );
		var rightSignature = Editor.EditorUtility.CreateAnimationRigsetSkeletonSignature( right );

		Assert.AreNotEqual( leftSignature.Hash, rightSignature.Hash );
	}

	[TestMethod]
	public void CompatibleSkeletonPassesValidation()
	{
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var skeleton ) );
		var signature = Editor.EditorUtility.CreateAnimationRigsetSkeletonSignature( skeleton );

		Assert.IsTrue( Editor.EditorUtility.IsAnimationRigsetSkeletonCompatible( signature, skeleton, out var reason ), reason );
	}

	[TestMethod]
	public void IncompatibleSkeletonFailsValidation()
	{
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var rigsetSkeleton ) );
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton( childName: "other_child" ), out var modelSkeleton ) );
		var signature = Editor.EditorUtility.CreateAnimationRigsetSkeletonSignature( rigsetSkeleton );

		Assert.IsFalse( Editor.EditorUtility.IsAnimationRigsetSkeletonCompatible( signature, modelSkeleton, out var reason ) );
		StringAssert.Contains( reason, "name mismatch" );
	}

	[TestMethod]
	public void RigsetAnimationSidecarNamesAreStable()
	{
		var animation = new Editor.EditorUtility.CastAnimationData { Name = "walk run" };

		Assert.AreEqual( "walk_run.dmx", Editor.EditorUtility.CreateRigsetAnimationSidecarFileName( animation ) );
	}

	[TestMethod]
	public void RigsetDefaultSidecarFormatIsDmx()
	{
		Assert.AreEqual( "dmx", new Sandbox.AnimationRigset().SidecarFormat );
	}

	[TestMethod]
	public void BasicOnlyRigsetSidecarFormatIsSmd()
	{
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var skeleton ) );
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "basic", Skeleton = skeleton };
		var animation = CreateScaleAnimation( skeleton );
		var context = new Editor.EditorUtility.CastImportContext( "basic.rigset" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.BasicOnly,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsTrue( Editor.EditorUtility.TrySelectRigsetSidecarFormat( sourceData, [animation], options, context, out var sidecarFormat ) );
		Assert.AreEqual( "smd", sidecarFormat );
		Assert.AreEqual( 0, context.Warnings.Count );
	}

	[TestMethod]
	public void AdvancedRigsetSidecarFormatIsDmxWhenSupported()
	{
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var skeleton ) );
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "advanced", Skeleton = skeleton };
		var animation = CreateScaleAnimation( skeleton );
		var context = new Editor.EditorUtility.CastImportContext( "advanced.rigset" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.AdvancedWhenSupported,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsTrue( Editor.EditorUtility.TrySelectRigsetSidecarFormat( sourceData, [animation], options, context, out var sidecarFormat ) );
		Assert.AreEqual( "dmx", sidecarFormat );
		Assert.AreEqual( 0, context.Warnings.Count );
	}

	[TestMethod]
	public void AdvancedRigsetSidecarFormatDoesNotFallBackForUnsupportedEvents()
	{
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var skeleton ) );
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "advanced", Skeleton = skeleton };
		var animation = CreateEventAnimation( skeleton );
		var context = new Editor.EditorUtility.CastImportContext( "advanced.rigset" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.AdvancedWhenSupported,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsFalse( Editor.EditorUtility.TrySelectRigsetSidecarFormat( sourceData, [animation], options, context, out _ ) );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "cannot be preserved", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void StrictRigsetSidecarWriteFailsForUnsupportedEvents()
	{
		using var temp = new TempRigsetDirectory();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var skeleton ) );
		var animation = CreateEventAnimation( skeleton );
		var context = new Editor.EditorUtility.CastImportContext( temp.RigsetPath );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.StrictAdvanced,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsFalse( Editor.EditorUtility.TryWriteRigsetSidecarFiles( temp.RigsetPath, skeleton, [animation], context, out _, options ) );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "cannot be preserved", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void IncompatibleRigsetStillAppendsAnimationReferences()
	{
		using var temp = new TempRigsetDirectory();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var modelSkeleton ) );
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton( childName: "other_child" ), out var rigsetSkeleton ) );
		var rigset = temp.CreateRigset( rigsetSkeleton, "idle" );
		var context = new Editor.EditorUtility.CastImportContext( temp.RigsetPath );
		var generated = new List<Editor.EditorUtility.CastGeneratedAnimationFile>();
		var references = new[]
		{
			new Editor.EditorUtility.RigsetReferenceData { Path = temp.RigsetPath, Rigset = rigset }
		};

		Assert.IsTrue( Editor.EditorUtility.TryAppendRigsetAnimations( modelSkeleton, references, generated, context ) );
		Assert.AreEqual( 1, generated.Count );
		Assert.AreEqual( "idle", generated[0].Animation.Name );
		Assert.AreEqual( temp.SidecarRelativePath, generated[0].RelativePath );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "not compatible", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void DuplicateRigsetReferencesDoNotDuplicateAnimations()
	{
		using var temp = new TempRigsetDirectory();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var skeleton ) );
		var rigset = temp.CreateRigset( skeleton, "idle" );
		var context = new Editor.EditorUtility.CastImportContext( temp.RigsetPath );
		var generated = new List<Editor.EditorUtility.CastGeneratedAnimationFile>();
		var references = new[]
		{
			new Editor.EditorUtility.RigsetReferenceData { Path = temp.RigsetPath, Rigset = rigset },
			new Editor.EditorUtility.RigsetReferenceData { Path = temp.RigsetPath, Rigset = rigset }
		};

		Assert.IsTrue( Editor.EditorUtility.TryAppendRigsetAnimations( skeleton, references, generated, context ) );
		Assert.AreEqual( 1, generated.Count );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "duplicate rigset", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void DuplicateRigsetAnimationNamesGetStableSuffixes()
	{
		using var temp = new TempRigsetDirectory();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var skeleton ) );
		var rigset = temp.CreateRigset( skeleton, "idle" );
		var context = new Editor.EditorUtility.CastImportContext( temp.RigsetPath );
		var generated = new List<Editor.EditorUtility.CastGeneratedAnimationFile>
		{
			new(
				new Editor.EditorUtility.CastAnimationData { Name = "idle" },
				"local.dmx" )
		};
		var references = new[]
		{
			new Editor.EditorUtility.RigsetReferenceData { Path = temp.RigsetPath, Rigset = rigset }
		};

		Assert.IsTrue( Editor.EditorUtility.TryAppendRigsetAnimations( skeleton, references, generated, context ) );
		Assert.AreEqual( 2, generated.Count );
		Assert.AreEqual( "idle_001", generated[1].Animation.Name );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "Renaming duplicate", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void RigsetAnimationsAppendToGeneratedModelDoc()
	{
		using var temp = new TempRigsetDirectory();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( CreateTwoBoneSkeleton(), out var skeleton ) );
		var rigset = temp.CreateRigset( skeleton, "run" );
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "model", Skeleton = skeleton };
		var modelDoc = Editor.EditorUtility.CreateCastModelDocTextForTests( sourceData, [] );
		var context = new Editor.EditorUtility.CastImportContext( temp.RigsetPath );
		var references = new[]
		{
			new Editor.EditorUtility.RigsetReferenceData { Path = temp.RigsetPath, Rigset = rigset }
		};

		Assert.IsTrue( Editor.EditorUtility.TryAppendRigsetAnimationsToModelDoc( modelDoc, references, skeleton, context, out var merged ) );
		StringAssert.Contains( merged, "name = \"run\"" );
		StringAssert.Contains( merged, "source_filename = \"" + temp.SidecarRelativePath.Replace( "\\", "\\\\" ) + "\"" );
	}

	static SkeletonNode CreateTwoBoneSkeleton( string childName = "child" )
	{
		var skeleton = new SkeletonNode();

		var root = skeleton.AddNode( new BoneNode( 0x1001 ) );
		root.Name = "root";
		root.ParentIndex = -1;
		root.LocalPosition = NumVec3.Zero;
		root.LocalRotation = NumQuat.Identity;

		var child = skeleton.AddNode( new BoneNode( 0x1002 ) );
		child.Name = childName;
		child.ParentIndex = 0;
		child.LocalPosition = new NumVec3( 1, 0, 0 );
		child.LocalRotation = NumQuat.Identity;

		return skeleton;
	}

	static Editor.EditorUtility.CastAnimationData CreateEventAnimation( Editor.EditorUtility.CastSkeletonData skeleton )
	{
		return new Editor.EditorUtility.CastAnimationData
		{
			Name = "events",
			Events = [new Editor.EditorUtility.CastAnimationEventData( "step", 1 )],
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeleton.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
	}

	static Editor.EditorUtility.CastAnimationData CreateScaleAnimation( Editor.EditorUtility.CastSkeletonData skeleton )
	{
		return new Editor.EditorUtility.CastAnimationData
		{
			Name = "scale",
			HasScaleKeys = true,
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeleton.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
	}

	sealed class TempRigsetDirectory : IDisposable
	{
		public TempRigsetDirectory()
		{
			Root = Path.Combine( Path.GetTempPath(), "sbox-rigset-tests", Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( Root );
			RigsetPath = Path.Combine( Root, "test.rigset" );
		}

		public string Root { get; }
		public string RigsetPath { get; }
		public string SidecarRelativePath { get; private set; }

		public Sandbox.AnimationRigset CreateRigset( Editor.EditorUtility.CastSkeletonData skeleton, string animationName )
		{
			var sidecarPath = Path.Combine( Root, $"{animationName}.dmx" );
			File.WriteAllText( sidecarPath, "<!-- dmx encoding keyvalues2 4 format model 22 -->\n" );
			SidecarRelativePath = sidecarPath.Replace( '\\', '/' );

			return new Sandbox.AnimationRigset
			{
				SidecarFormat = "dmx",
				SkeletonSignature = Editor.EditorUtility.CreateAnimationRigsetSkeletonSignature( skeleton ),
				Animations =
				[
					new Sandbox.AnimationRigsetAnimation
					{
						Name = animationName,
						FrameRate = 30.0f,
						Looping = false,
						SidecarPath = SidecarRelativePath
					}
				]
			};
		}

		public void Dispose()
		{
			if ( Directory.Exists( Root ) )
				Directory.Delete( Root, true );
		}
	}
}
