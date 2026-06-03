using Cast.NET;
using Cast.NET.Nodes;
using NumVec3 = System.Numerics.Vector3;

[TestClass]
public class CastModelImportTests
{
	[TestMethod]
	public void MissingScaleKeepsVertexPositions()
	{
		var model = new ModelNode();
		var mesh = model.AddNode<MeshNode>();
		mesh.VertexPositionBuffer = new CastArrayProperty<NumVec3>(
		[
			new( 0, 0, 0 ),
			new( 1, 0, 0 ),
			new( 0, 1, 0 )
		] );
		mesh.FaceBuffer = new CastArrayProperty<uint>( [0, 1, 2] );

		Assert.IsFalse( model.Properties.ContainsKey( "s" ) );
		Assert.IsTrue( Editor.EditorUtility.TryCreateCastMeshData( model, mesh, out var data ) );
		Assert.AreEqual( new Vector3( 1, 0, 0 ), data.Positions[1] );
		Assert.AreEqual( new Vector3( 0, 1, 0 ), data.Positions[2] );
	}

	[TestMethod]
	public void MissingStreamsAndInvalidFacesAreTolerated()
	{
		var model = new ModelNode();
		var mesh = model.AddNode<MeshNode>();
		mesh.VertexPositionBuffer = new CastArrayProperty<NumVec3>(
		[
			new( 0, 0, 0 ),
			new( 1, 0, 0 ),
			new( 0, 1, 0 )
		] );
		mesh.FaceBuffer = new CastArrayProperty<uint>(
		[
			0, 1, 2,
			0, 0, 1,
			0, 1, 7
		] );

		Assert.IsTrue( Editor.EditorUtility.TryCreateCastMeshData( model, mesh, out var data ) );
		Assert.AreEqual( 1, data.Faces.Length );
		Assert.AreEqual( 1, data.SkippedDegenerateFaces );
		Assert.AreEqual( 1, data.SkippedOutOfRangeFaces );
		Assert.AreEqual( 3, data.Normals.Length );
		Assert.AreEqual( 3, data.TexCoords.Length );
		Assert.AreNotEqual( Vector3.Zero, data.Normals[0] );
		Assert.AreEqual( Vector2.Zero, data.TexCoords[0] );
	}
}
