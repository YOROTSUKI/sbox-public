using Cast.NET;
using Cast.NET.Nodes;
using System;
using System.Collections.Generic;
using System.Globalization;
using NumQuat = System.Numerics.Quaternion;
using NumVec3 = System.Numerics.Vector3;
using NumVec4 = System.Numerics.Vector4;

[TestClass]
public class CastAnimationImportTests
{
	[TestMethod]
	public void BaseCastAnimationsAreCollected()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var cast = new Cast.NET.Cast();
		cast.AddNode( CreateTranslationAnimation( "root", 2.0f ) );

		var context = new Editor.EditorUtility.CastImportContext( "base.cast" );
		var animations = new List<Editor.EditorUtility.CastAnimationData>();
		var usedNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		Editor.EditorUtility.AddCastAnimations( cast, skeletonData, "base", context, animations, usedNames );

		Assert.AreEqual( 1, animations.Count );
		Assert.AreEqual( "base", animations[0].Name );
		Assert.AreEqual( 2.0f, animations[0].Frames[0].BoneTransforms[0].Position.x );
	}

	[TestMethod]
	public void DuplicateAnimationNamesGetStableSuffixes()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var cast = new Cast.NET.Cast();
		cast.AddNode( CreateTranslationAnimation( "root", 1.0f ) );
		cast.AddNode( CreateTranslationAnimation( "child", 2.0f ) );

		var context = new Editor.EditorUtility.CastImportContext( "idle.cast" );
		var animations = new List<Editor.EditorUtility.CastAnimationData>();
		var usedNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		Editor.EditorUtility.AddCastAnimations( cast, skeletonData, "idle", context, animations, usedNames );

		Assert.AreEqual( 2, animations.Count );
		Assert.AreEqual( "idle", animations[0].Name );
		Assert.AreEqual( "idle_001", animations[1].Name );
	}

	[TestMethod]
	public void InvalidAnimationsAreNotAdded()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var cast = new Cast.NET.Cast();
		cast.AddNode( CreateTranslationAnimation( "ghost", 1.0f ) );

		var context = new Editor.EditorUtility.CastImportContext( "bad.cast" );
		var animations = new List<Editor.EditorUtility.CastAnimationData>();
		var usedNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		Editor.EditorUtility.AddCastAnimations( cast, skeletonData, "bad", context, animations, usedNames );

		Assert.AreEqual( 0, animations.Count );
		Assert.AreEqual( 0, usedNames.Count );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "unknown bone", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void CastFileScorePrioritizesSkeletonCapableBase()
	{
		var meshSkeleton = new Editor.EditorUtility.CastFileSummary( HasModelNode: true, HasMesh: true, HasSkeleton: true, HasWeightData: true, HasAnimation: false );
		var skeletonOnly = new Editor.EditorUtility.CastFileSummary( HasModelNode: false, HasMesh: false, HasSkeleton: true, HasWeightData: false, HasAnimation: false );
		var meshWeights = new Editor.EditorUtility.CastFileSummary( HasModelNode: true, HasMesh: true, HasSkeleton: false, HasWeightData: true, HasAnimation: false );
		var meshOnlyAnimated = new Editor.EditorUtility.CastFileSummary( HasModelNode: true, HasMesh: true, HasSkeleton: false, HasWeightData: false, HasAnimation: true );

		Assert.IsTrue( Editor.CreateAnimatedModelFromCastDialog.ScoreCastFile( meshSkeleton ) > Editor.CreateAnimatedModelFromCastDialog.ScoreCastFile( skeletonOnly ) );
		Assert.IsTrue( Editor.CreateAnimatedModelFromCastDialog.ScoreCastFile( skeletonOnly ) > Editor.CreateAnimatedModelFromCastDialog.ScoreCastFile( meshWeights ) );
		Assert.IsTrue( Editor.CreateAnimatedModelFromCastDialog.ScoreCastFile( meshWeights ) > Editor.CreateAnimatedModelFromCastDialog.ScoreCastFile( meshOnlyAnimated ) );
	}

	[TestMethod]
	public void RootBoneParentIndexMinusOneIsPreserved()
	{
		var skeleton = CreateTwoBoneSkeleton();

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		Assert.AreEqual( -1, skeletonData.Bones[0].ParentIndex );
		Assert.AreEqual( 0, skeletonData.Bones[1].ParentIndex );
	}

	[TestMethod]
	public void MissingScaleDefaultsToOne()
	{
		var skeleton = new SkeletonNode();
		var bone = skeleton.AddNode<BoneNode>();
		bone.Name = "root";
		bone.ParentIndex = -1;
		bone.LocalPosition = new NumVec3( 1, 2, 3 );
		bone.LocalRotation = NumQuat.Identity;

		Assert.IsFalse( bone.Properties.ContainsKey( "s" ) );
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		Assert.AreEqual( Vector3.One, skeletonData.Bones[0].LocalTransform.Scale );
	}

	[TestMethod]
	public void MissingCurveUsesBindPose()
	{
		var skeleton = CreateTwoBoneSkeleton();
		var animation = new AnimationNode
		{
			Framerate = 24.0f,
			Looping = true,
			Curves =
			[
				CreateFloatCurve( "root", "tx", "absolute", [0, 1], [5.0f, 7.0f] )
			]
		};

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		var context = new Editor.EditorUtility.CastImportContext( "test" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastAnimationData( animation, skeletonData, "idle", context, out var animationData ) );
		Assert.AreEqual( 24.0f, animationData.FrameRate );
		Assert.IsTrue( animationData.Looping );
		Assert.IsTrue( animationData.Frames[1].BoneTransforms[1].AlmostEqual( skeletonData.Bones[1].LocalTransform ) );
	}

	[TestMethod]
	public void MismatchedKeyValueCountsAreSkippedGracefully()
	{
		var skeleton = CreateTwoBoneSkeleton();
		var animation = new AnimationNode
		{
			Curves =
			[
				CreateFloatCurve( "root", "tx", "absolute", [0, 1], [5.0f, 7.0f] ),
				CreateFloatCurve( "root", "ty", "absolute", [0, 1], [3.0f] )
			]
		};

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		var context = new Editor.EditorUtility.CastImportContext( "test" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastAnimationData( animation, skeletonData, "idle", context, out var animationData ) );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "key/value counts", StringComparison.OrdinalIgnoreCase ) ) );
		Assert.AreEqual( 7.0f, animationData.Frames[1].BoneTransforms[0].Position.x );
		Assert.AreEqual( skeletonData.Bones[0].LocalTransform.Position.y, animationData.Frames[1].BoneTransforms[0].Position.y );
	}

	[TestMethod]
	public void UnknownBoneCurveIsSkipped()
	{
		var skeleton = CreateTwoBoneSkeleton();
		var animation = new AnimationNode
		{
			Curves =
			[
				CreateFloatCurve( "ghost", "tx", "absolute", [0], [9.0f] ),
				CreateFloatCurve( "root", "tx", "absolute", [0], [5.0f] )
			]
		};

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		var context = new Editor.EditorUtility.CastImportContext( "test" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastAnimationData( animation, skeletonData, "idle", context, out var animationData ) );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "unknown bone", StringComparison.OrdinalIgnoreCase ) ) );
		Assert.AreEqual( 5.0f, animationData.Frames[0].BoneTransforms[0].Position.x );
	}

	[TestMethod]
	public void QuaternionNormalizationIsApplied()
	{
		var skeleton = CreateTwoBoneSkeleton();
		var animation = new AnimationNode
		{
			Curves =
			[
				CreateQuaternionCurve( "root", "rq", "absolute", [0], [new NumVec4( 0, 0, 0, 10 )] )
			]
		};

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		var context = new Editor.EditorUtility.CastImportContext( "test" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastAnimationData( animation, skeletonData, "idle", context, out var animationData ) );
		Assert.IsTrue( animationData.Frames[0].BoneTransforms[0].Rotation.AlmostEqual( Rotation.Identity ) );
	}

	[TestMethod]
	public void SmdAnimationRotationsAreWrittenInXyzAxisOrder()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "axes",
			Frames =
			[
				CreateSingleRootRotationFrame( skeletonData, NumVec3.UnitX ),
				CreateSingleRootRotationFrame( skeletonData, NumVec3.UnitY ),
				CreateSingleRootRotationFrame( skeletonData, NumVec3.UnitZ )
			]
		};

		var smd = Editor.EditorUtility.CreateAnimationCastSmdTextForTests( skeletonData, animation );
		var rootLines = smd
			.Split( '\n' )
			.Select( x => x.Trim() )
			.Where( x => x.StartsWith( "0 ", StringComparison.Ordinal ) && x.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Length == 7 )
			.ToArray();

		Assert.AreEqual( 3, rootLines.Length );
		AssertSmdRotation( rootLines[0], MathF.PI * 0.5f, 0.0f, 0.0f );
		AssertSmdRotation( rootLines[1], 0.0f, MathF.PI * 0.5f, 0.0f );
		AssertSmdRotation( rootLines[2], 0.0f, 0.0f, MathF.PI * 0.5f );
	}

	[TestMethod]
	public void TwoBoneAnimationProducesExpectedFrameTransforms()
	{
		var skeleton = CreateTwoBoneSkeleton();
		var rootDelta = NumQuat.CreateFromAxisAngle( NumVec3.UnitZ, MathF.PI * 0.5f );
		var animation = new AnimationNode
		{
			Framerate = 30.0f,
			Looping = false,
			Curves =
			[
				CreateFloatCurve( "child", "tx", "absolute", [0, 1], [1.0f, 3.0f] ),
				CreateQuaternionCurve( "root", "rq", "additive", [0, 1], [new NumVec4( 0, 0, 0, 1 ), new NumVec4( rootDelta.X, rootDelta.Y, rootDelta.Z, rootDelta.W )] )
			]
		};

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		var context = new Editor.EditorUtility.CastImportContext( "test" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastAnimationData( animation, skeletonData, "turn", context, out var animationData ) );
		Assert.AreEqual( 2, animationData.Frames.Length );
		Assert.AreEqual( 3.0f, animationData.Frames[1].BoneTransforms[1].Position.x );
		Assert.IsTrue( animationData.Frames[1].BoneTransforms[0].Rotation.AlmostEqual( (Rotation)rootDelta ) );
	}

	[TestMethod]
	public void ScaleCurvesAreFlagged()
	{
		var skeleton = CreateTwoBoneSkeleton();
		var animation = new AnimationNode
		{
			Curves =
			[
				CreateVector3Curve( "root", "s", "absolute", [0], [new NumVec3( 2, 3, 4 )] )
			]
		};

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		var context = new Editor.EditorUtility.CastImportContext( "test" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastAnimationData( animation, skeletonData, "scale", context, out var animationData ) );
		Assert.IsTrue( animationData.HasScaleKeys );
		Assert.AreEqual( new Vector3( 2, 3, 4 ), animationData.Frames[0].BoneTransforms[0].Scale );
	}

	[TestMethod]
	public void NotificationEventsAreDedupedAndEmptyNamesAreSkipped()
	{
		var skeleton = CreateTwoBoneSkeleton();
		var animation = new AnimationNode
		{
			Curves =
			[
				CreateFloatCurve( "root", "tx", "absolute", [0], [1.0f] )
			],
			NotificationTracks =
			[
				CreateNotificationTrack( "step", [3, 1, 3] ),
				CreateNotificationTrack( "", [2] )
			]
		};

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		var context = new Editor.EditorUtility.CastImportContext( "test" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastAnimationData( animation, skeletonData, "events", context, out var animationData ) );
		Assert.AreEqual( 2, animationData.Events.Count );
		Assert.AreEqual( new Editor.EditorUtility.CastAnimationEventData( "step", 1 ), animationData.Events[0] );
		Assert.AreEqual( new Editor.EditorUtility.CastAnimationEventData( "step", 3 ), animationData.Events[1] );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "empty name", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void BlendShapeDeltasAreComputedAfterModelTransform()
	{
		var cast = CreateBlendShapeCast();
		var context = new Editor.EditorUtility.CastImportContext( "blend.cast" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSourceData( cast, "blend", context, out var sourceData ) );
		Assert.AreEqual( 1, sourceData.BlendShapes.Length );
		Assert.AreEqual( "smile", sourceData.BlendShapes[0].Name );
		Assert.AreEqual( 1, sourceData.BlendShapes[0].VertexDeltas.Length );
		Assert.AreEqual( 1, sourceData.BlendShapes[0].VertexDeltas[0].VertexIndex );
		Assert.AreEqual( 2.0f, sourceData.BlendShapes[0].VertexDeltas[0].Delta.x );
		Assert.AreEqual( 0.0f, sourceData.BlendShapes[0].VertexDeltas[0].Delta.y );
		Assert.AreEqual( 0.0f, sourceData.BlendShapes[0].VertexDeltas[0].Delta.z );
	}

	[TestMethod]
	public void CastMeshDataKeepsStaticFaceVertexAttributesExpanded()
	{
		var model = new ModelNode();
		var mesh = model.AddNode<MeshNode>();
		mesh.VertexPositionBuffer = new CastArrayProperty<NumVec3>(
		[
			new( 0, 0, 0 ),
			new( 1, 0, 0 ),
			new( 1, 1, 0 ),
			new( 0, 1, 0 )
		] );
		mesh.VertexNormalBuffer = new CastArrayProperty<NumVec3>(
		[
			NumVec3.UnitZ,
			NumVec3.UnitZ,
			NumVec3.UnitZ,
			NumVec3.UnitZ
		] );
		mesh.FaceBuffer = new CastArrayProperty<byte>( [0, 1, 2, 0, 2, 3] );
		mesh.AddUVLayer( 0, new CastArrayProperty<System.Numerics.Vector2>(
		[
			new( 0, 0 ),
			new( 1, 0 ),
			new( 1, 1 ),
			new( 0, 1 )
		] ) );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastMeshData( model, mesh, out var meshData ) );
		Assert.AreEqual( 4, meshData.Positions.Length );
		Assert.AreEqual( 4, meshData.Normals.Length );
		Assert.AreEqual( 4, meshData.TexCoords.Length );
		Assert.AreEqual( 2, meshData.Faces.Length );
		Assert.AreEqual( 6, meshData.FaceVertexNormals.Length );
		Assert.AreEqual( 6, meshData.FaceVertexTexCoords.Length );
		Assert.AreEqual( new Vector2( 0, 0 ), meshData.FaceVertexTexCoords[0] );
		Assert.AreEqual( new Vector2( 1, 1 ), meshData.FaceVertexTexCoords[2] );
		Assert.AreEqual( new Vector2( 0, 0 ), meshData.FaceVertexTexCoords[3] );
		Assert.AreEqual( new Vector2( 0, 1 ), meshData.FaceVertexTexCoords[5] );
	}

	[TestMethod]
	public void IkAndConstraintsResolveBoneHashes()
	{
		var skeleton = CreateHashedThreeBoneSkeleton();
		var ik = skeleton.AddNode<IKHandleNode>();
		ik.Name = "leg";
		ik.StartBoneHash = 0x1001;
		ik.EndBoneHash = 0x1002;
		ik.TargetBoneHash = 0x1003;
		ik.UseTargetRotation = true;

		var constraint = skeleton.AddNode<ConstraintNode>();
		constraint.ConstraintType = "pt";
		constraint.ConstraintBoneHash = 0x1002;
		constraint.TargetBoneHash = 0x1001;
		constraint.MaintainOffset = true;
		constraint.Weight = 0.5f;

		var cast = new Cast.NET.Cast();
		cast.AddNode( skeleton );
		var context = new Editor.EditorUtility.CastImportContext( "advanced.cast" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSourceData( cast, "advanced", context, out var sourceData ) );
		Assert.AreEqual( 1, sourceData.IkHandles.Length );
		Assert.AreEqual( 0, sourceData.IkHandles[0].StartBoneIndex );
		Assert.AreEqual( 1, sourceData.IkHandles[0].EndBoneIndex );
		Assert.AreEqual( 2, sourceData.IkHandles[0].TargetBoneIndex );
		Assert.IsTrue( sourceData.IkHandles[0].UseTargetRotation );
		Assert.AreEqual( 1, sourceData.Constraints.Length );
		Assert.AreEqual( 1, sourceData.Constraints[0].ConstraintBoneIndex );
		Assert.AreEqual( 0, sourceData.Constraints[0].TargetBoneIndex );
		Assert.IsTrue( sourceData.Constraints[0].MaintainOffset );
	}

	[TestMethod]
	public void MissingAdvancedReferencesWarnAndAreSkipped()
	{
		var skeleton = CreateHashedThreeBoneSkeleton();
		var ik = skeleton.AddNode<IKHandleNode>();
		ik.Name = "bad";
		ik.StartBoneHash = 0x9999;
		ik.EndBoneHash = 0x1002;

		var cast = new Cast.NET.Cast();
		cast.AddNode( skeleton );
		var context = new Editor.EditorUtility.CastImportContext( "missing.cast" );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSourceData( cast, "missing", context, out var sourceData ) );
		Assert.AreEqual( 0, sourceData.IkHandles.Length );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "0x0000000000009999", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void DmxMeshTextUsesFaceVertexNormalAndUvStreams()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );
		var sourceData = new Editor.EditorUtility.CastSourceData
		{
			Name = "seams",
			Skeleton = skeletonData,
			Meshes = [CreateFaceVertexSeamMesh()]
		};

		var dmx = Editor.EditorUtility.CreateBaseCastDmxTextForTests( sourceData );

		CollectionAssert.AreEqual(
			new[] { "0", "1", "2", "0", "2", "3" },
			GetDmxArrayValues( dmx, "position$0Indices" ) );
		CollectionAssert.AreEqual(
			new[] { "0 0 1", "0 0 1", "0 0 1", "1 0 0", "1 0 0", "1 0 0" },
			GetDmxArrayValues( dmx, "normal$0" ) );
		CollectionAssert.AreEqual(
			new[] { "0", "1", "2", "3", "4", "5" },
			GetDmxArrayValues( dmx, "normal$0Indices" ) );
		CollectionAssert.AreEqual(
			new[] { "0 0", "1 0", "1 1", "0.25 0.25", "0.75 0.75", "0 1" },
			GetDmxArrayValues( dmx, "texcoord$0" ) );
		CollectionAssert.AreEqual(
			new[] { "0", "1", "2", "3", "4", "5" },
			GetDmxArrayValues( dmx, "texcoord$0Indices" ) );
	}

	[TestMethod]
	public void BasicOnlyModelImportSelectsSmd()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "basic", Skeleton = skeletonData };
		var context = new Editor.EditorUtility.CastImportContext( "basic.cast" );

		Assert.IsTrue( Editor.EditorUtility.TrySelectCastModelSourceWriter( sourceData, [], Editor.CastAnimatedModelImportOptions.BasicOnly, context, out var writer ) );
		Assert.AreEqual( "SMD", writer.Name );
	}

	[TestMethod]
	public void BasicOnlyModelImportWithScaleKeysSelectsSmd()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "scale",
			HasScaleKeys = true,
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "basic", Skeleton = skeletonData };
		var context = new Editor.EditorUtility.CastImportContext( "basic.cast" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.BasicOnly,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsTrue( Editor.EditorUtility.TrySelectCastModelSourceWriter( sourceData, [animation], options, context, out var writer ) );
		Assert.AreEqual( "SMD", writer.Name );
		Assert.AreEqual( 0, context.Warnings.Count );
	}

	[TestMethod]
	public void AdvancedWhenSupportedAllowsScaleWithDmxWriter()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "scale",
			HasScaleKeys = true,
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "advanced", Skeleton = skeletonData };
		var context = new Editor.EditorUtility.CastImportContext( "advanced.cast" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.AdvancedWhenSupported,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsTrue( Editor.EditorUtility.TrySelectCastModelSourceWriter( sourceData, [animation], options, context, out var writer ) );
		Assert.AreEqual( "DMX", writer.Name );
		Assert.AreEqual( 0, context.Warnings.Count );
	}

	[TestMethod]
	public void StrictAdvancedAllowsScaleWithDmxWriter()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "scale",
			HasScaleKeys = true,
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "strict", Skeleton = skeletonData };
		var context = new Editor.EditorUtility.CastImportContext( "strict.cast" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.StrictAdvanced,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsTrue( Editor.EditorUtility.TrySelectCastModelSourceWriter( sourceData, [animation], options, context, out var writer ) );
		Assert.AreEqual( "DMX", writer.Name );
		Assert.AreEqual( 0, context.Warnings.Count );
	}

	[TestMethod]
	public void AdvancedWhenSupportedDoesNotFallBackWhenBlendShapesCannotBePreserved()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var sourceData = CreateBlendShapeSourceData( skeletonData );
		var context = new Editor.EditorUtility.CastImportContext( "advanced.cast" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.AdvancedWhenSupported,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsFalse( Editor.EditorUtility.TrySelectCastModelSourceWriter( sourceData, [], options, context, out _ ) );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "cannot be preserved", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void StrictAdvancedFailsWhenBlendShapesCannotBePreserved()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var sourceData = CreateBlendShapeSourceData( skeletonData );
		var context = new Editor.EditorUtility.CastImportContext( "strict.cast" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.StrictAdvanced,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsFalse( Editor.EditorUtility.TrySelectCastModelSourceWriter( sourceData, [], options, context, out _ ) );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "cannot be preserved", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void StrictAdvancedFailsWhenNotificationEventsCannotBePreserved()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "events",
			Events = [new Editor.EditorUtility.CastAnimationEventData( "step", 1 )],
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "strict", Skeleton = skeletonData };
		var context = new Editor.EditorUtility.CastImportContext( "strict.cast" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.StrictAdvanced,
			RootMotionMode = Editor.CastRootMotionMode.None
		};

		Assert.IsFalse( Editor.EditorUtility.TrySelectCastModelSourceWriter( sourceData, [animation], options, context, out _ ) );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "cannot be preserved", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void StrictAdvancedFailsWhenRootMotionCannotBePreserved()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "root_motion",
			RootMotion = new Editor.EditorUtility.CastRootMotionData( "root", 0 ),
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "strict", Skeleton = skeletonData };
		var context = new Editor.EditorUtility.CastImportContext( "strict.cast" );
		var options = new Editor.CastAnimatedModelImportOptions
		{
			AdvancedDataMode = Editor.CastAdvancedDataMode.StrictAdvanced,
			RootMotionMode = Editor.CastRootMotionMode.Bone,
			RootMotionBoneName = "root"
		};

		Assert.IsFalse( Editor.EditorUtility.TrySelectCastModelSourceWriter( sourceData, [animation], options, context, out _ ) );
		Assert.IsTrue( context.Warnings.Any( x => x.Contains( "cannot be preserved", StringComparison.OrdinalIgnoreCase ) ) );
	}

	[TestMethod]
	public void DmxModelDocEnablesScaleForScaleAnimations()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "scale",
			HasScaleKeys = true,
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "dmx", Skeleton = skeletonData };

		var modelDoc = Editor.EditorUtility.CreateCastDmxModelDocTextForTests( sourceData, [animation] );
		StringAssert.Contains( modelDoc, "filename = \"model.dmx\"" );
		StringAssert.Contains( modelDoc, "source_filename = \"scale.dmx\"" );
		StringAssert.Contains( modelDoc, "enable_scale = true" );
	}

	[TestMethod]
	public void DmxAnimationTextContainsScaleChannelValues()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var frameTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray();
		frameTransforms[0].Scale = new Vector3( 2, 3, 4 );
		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "scale",
			HasScaleKeys = true,
			FrameRate = 30.0f,
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = frameTransforms }]
		};

		var dmx = Editor.EditorUtility.CreateAnimationCastDmxTextForTests( skeletonData, animation );
		StringAssert.Contains( dmx, "<!-- dmx encoding keyvalues2 4 format model 22 -->" );
		StringAssert.Contains( dmx, "\"toAttribute\" \"string\" \"scale\"" );
		StringAssert.Contains( dmx, "\"2 3 4\"" );
		Assert.IsFalse( dmx.Contains( "\"compressed\"", StringComparison.OrdinalIgnoreCase ) );

		var timeFrameIndex = dmx.IndexOf( "\"timeFrame\"", StringComparison.Ordinal );
		Assert.IsTrue( timeFrameIndex > 0 );
		Assert.IsFalse( dmx[..timeFrameIndex].TrimEnd().EndsWith( "},", StringComparison.Ordinal ) );
	}

	[TestMethod]
	public void BasicOnlyModelDocKeepsScaleDisabled()
	{
		var skeleton = CreateTwoBoneSkeleton();
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastSkeletonData( skeleton, out var skeletonData ) );

		var animation = new Editor.EditorUtility.CastAnimationData
		{
			Name = "scale",
			HasScaleKeys = true,
			Frames = [new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray() }]
		};
		var sourceData = new Editor.EditorUtility.CastSourceData { Name = "smd", Skeleton = skeletonData };

		var modelDoc = Editor.EditorUtility.CreateCastModelDocTextForTests( sourceData, [animation] );
		StringAssert.Contains( modelDoc, "enable_scale = false" );
		Assert.IsFalse( modelDoc.Contains( "enable_scale = true", StringComparison.OrdinalIgnoreCase ) );
	}

	static SkeletonNode CreateTwoBoneSkeleton()
	{
		var skeleton = new SkeletonNode();

		var root = skeleton.AddNode<BoneNode>();
		root.Name = "root";
		root.ParentIndex = -1;
		root.LocalPosition = NumVec3.Zero;
		root.LocalRotation = NumQuat.Identity;

		var child = skeleton.AddNode<BoneNode>();
		child.Name = "child";
		child.ParentIndex = 0;
		child.LocalPosition = new NumVec3( 1, 0, 0 );
		child.LocalRotation = NumQuat.Identity;

		return skeleton;
	}

	static SkeletonNode CreateHashedThreeBoneSkeleton()
	{
		var skeleton = new SkeletonNode();

		var root = skeleton.AddNode( new BoneNode( 0x1001 ) );
		root.Name = "root";
		root.ParentIndex = -1;
		root.LocalPosition = NumVec3.Zero;
		root.LocalRotation = NumQuat.Identity;

		var knee = skeleton.AddNode( new BoneNode( 0x1002 ) );
		knee.Name = "knee";
		knee.ParentIndex = 0;
		knee.LocalPosition = new NumVec3( 0, 0, 1 );
		knee.LocalRotation = NumQuat.Identity;

		var target = skeleton.AddNode( new BoneNode( 0x1003 ) );
		target.Name = "target";
		target.ParentIndex = -1;
		target.LocalPosition = new NumVec3( 0, 1, 0 );
		target.LocalRotation = NumQuat.Identity;

		return skeleton;
	}

	static Cast.NET.Cast CreateBlendShapeCast()
	{
		var cast = new Cast.NET.Cast();
		var model = new ModelNode();
		model.Scale = new NumVec3( 2, 2, 2 );

		var mesh = model.AddNode( new MeshNode( 0x2001 ) );
		mesh.Name = "body";
		mesh.VertexPositionBuffer = new CastArrayProperty<NumVec3>(
		[
			new( 0, 0, 0 ),
			new( 1, 0, 0 ),
			new( 0, 1, 0 )
		] );
		mesh.VertexNormalBuffer = new CastArrayProperty<NumVec3>(
		[
			NumVec3.UnitZ,
			NumVec3.UnitZ,
			NumVec3.UnitZ
		] );
		mesh.FaceBuffer = new CastArrayProperty<byte>( [0, 1, 2] );
		mesh.AddUVLayer( 0, new CastArrayProperty<System.Numerics.Vector2>(
		[
			System.Numerics.Vector2.Zero,
			System.Numerics.Vector2.UnitX,
			System.Numerics.Vector2.UnitY
		] ) );

		var blendShape = model.AddNode( new BlendShapeNode( 0x3001 ) );
		blendShape.Name = "smile";
		blendShape.BaseShapeHash = 0x2001;
		blendShape.TargetShapeVertexIndices = new CastArrayProperty<byte>( [1] );
		blendShape.TargetShapeVertexPositions = new CastArrayProperty<NumVec3>( [new NumVec3( 2, 0, 0 )] );

		cast.AddNode( model );
		return cast;
	}

	static Editor.EditorUtility.CastMeshData CreateFaceVertexSeamMesh()
	{
		return new Editor.EditorUtility.CastMeshData
		{
			SourceHash = 0x2001,
			Name = "seams",
			MaterialName = "materials/dev/primary_white.vmat",
			Positions =
			[
				new( 0, 0, 0 ),
				new( 1, 0, 0 ),
				new( 1, 1, 0 ),
				new( 0, 1, 0 )
			],
			Faces =
			[
				new Editor.EditorUtility.CastTriangle( 0, 1, 2 ),
				new Editor.EditorUtility.CastTriangle( 0, 2, 3 )
			],
			Normals =
			[
				new( 0, 0, 1 ),
				new( 0, 0, 1 ),
				new( 0, 0, 1 ),
				new( 0, 0, 1 )
			],
			TexCoords =
			[
				new( 0, 0 ),
				new( 1, 0 ),
				new( 1, 1 ),
				new( 0, 1 )
			],
			FaceVertexNormals =
			[
				new( 0, 0, 1 ),
				new( 0, 0, 1 ),
				new( 0, 0, 1 ),
				new( 1, 0, 0 ),
				new( 1, 0, 0 ),
				new( 1, 0, 0 )
			],
			FaceVertexTexCoords =
			[
				new( 0, 0 ),
				new( 1, 0 ),
				new( 1, 1 ),
				new( 0.25f, 0.25f ),
				new( 0.75f, 0.75f ),
				new( 0, 1 )
			]
		};
	}

	static Editor.EditorUtility.CastSourceData CreateBlendShapeSourceData( Editor.EditorUtility.CastSkeletonData skeletonData )
	{
		return new Editor.EditorUtility.CastSourceData
		{
			Name = "blend",
			Skeleton = skeletonData,
			BlendShapes =
			[
				new Editor.EditorUtility.CastBlendShapeData
				{
					Name = "smile",
					VertexDeltas = [new Editor.EditorUtility.CastBlendShapeVertexDelta( 0, Vector3.Up )]
				}
			]
		};
	}

	static string[] GetDmxArrayValues( string dmx, string name )
	{
		var header = $"\"{name}\" ";
		var headerIndex = dmx.IndexOf( header, StringComparison.Ordinal );
		Assert.IsTrue( headerIndex >= 0, $"Missing DMX array \"{name}\"." );

		var startIndex = dmx.IndexOf( '[', headerIndex );
		Assert.IsTrue( startIndex >= 0, $"Missing DMX array start for \"{name}\"." );

		var endIndex = dmx.IndexOf( ']', startIndex );
		Assert.IsTrue( endIndex >= 0, $"Missing DMX array end for \"{name}\"." );

		return dmx[(startIndex + 1)..endIndex]
			.Split( ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries )
			.Select( x => x.Trim().TrimEnd( ',' ) )
			.Where( x => x.Length >= 2 && x[0] == '"' && x[^1] == '"' )
			.Select( x => x[1..^1] )
			.ToArray();
	}

	static AnimationNode CreateTranslationAnimation( string boneName, float value )
	{
		return new AnimationNode
		{
			Curves =
			[
				CreateFloatCurve( boneName, "tx", "absolute", [0], [value] )
			]
		};
	}

	static CurveNode CreateFloatCurve( string boneName, string keyName, string mode, byte[] frames, float[] values )
	{
		return new CurveNode
		{
			NodeName = boneName,
			KeyPropertyName = keyName,
			Mode = mode,
			KeyFrameBuffer = new CastArrayProperty<byte>( frames ),
			KeyValueBuffer = new CastArrayProperty<float>( values )
		};
	}

	static CurveNode CreateVector3Curve( string boneName, string keyName, string mode, byte[] frames, NumVec3[] values )
	{
		return new CurveNode
		{
			NodeName = boneName,
			KeyPropertyName = keyName,
			Mode = mode,
			KeyFrameBuffer = new CastArrayProperty<byte>( frames ),
			KeyValueBuffer = new CastArrayProperty<NumVec3>( values )
		};
	}

	static CurveNode CreateQuaternionCurve( string boneName, string keyName, string mode, byte[] frames, NumVec4[] values )
	{
		return new CurveNode
		{
			NodeName = boneName,
			KeyPropertyName = keyName,
			Mode = mode,
			KeyFrameBuffer = new CastArrayProperty<byte>( frames ),
			KeyValueBuffer = new CastArrayProperty<NumVec4>( values )
		};
	}

	static Editor.EditorUtility.CastAnimationFrameData CreateSingleRootRotationFrame( Editor.EditorUtility.CastSkeletonData skeletonData, NumVec3 axis )
	{
		var boneTransforms = skeletonData.Bones.Select( x => x.LocalTransform ).ToArray();
		boneTransforms[0].Rotation = (Rotation)NumQuat.CreateFromAxisAngle( axis, MathF.PI * 0.5f );

		return new Editor.EditorUtility.CastAnimationFrameData { BoneTransforms = boneTransforms };
	}

	static void AssertSmdRotation( string line, float expectedX, float expectedY, float expectedZ )
	{
		var parts = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		Assert.AreEqual( 7, parts.Length );
		AssertNearlyEqual( expectedX, float.Parse( parts[4], CultureInfo.InvariantCulture ) );
		AssertNearlyEqual( expectedY, float.Parse( parts[5], CultureInfo.InvariantCulture ) );
		AssertNearlyEqual( expectedZ, float.Parse( parts[6], CultureInfo.InvariantCulture ) );
	}

	static void AssertNearlyEqual( float expected, float actual )
	{
		Assert.IsTrue( MathF.Abs( expected - actual ) <= 0.0001f, $"Expected {expected} but got {actual}." );
	}

	static NotificationTrackNode CreateNotificationTrack( string name, byte[] frames )
	{
		return new NotificationTrackNode
		{
			Name = name,
			KeyFrameBuffer = new CastArrayProperty<byte>( frames )
		};
	}
}
