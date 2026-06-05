using System;
using System.IO;

namespace Editor;

/// <summary>
/// A popup dialog for creating reusable animation rigsets from CAST animation files.
/// </summary>
public class CreateAnimationRigsetFromCastDialog : Widget
{
	readonly Asset _baseCast;
	readonly List<Asset> _selectedAnimations;
	readonly bool _useAnimationFolder;
	readonly ComboBox _advancedDataCombo;
	readonly ComboBox _rootMotionModeCombo;
	readonly ComboBox _rootMotionBoneCombo;
	readonly LineEdit _fileEdit;
	readonly FolderEdit _animationFolderEdit;
	readonly Widget _animationFolderRow;
	readonly Widget _rootMotionModeRow;
	readonly Widget _rootMotionBoneRow;

	public CreateAnimationRigsetFromCastDialog( List<Asset> castFiles ) : base( null )
	{
		_baseCast = ResolveBaseCast( castFiles );
		_selectedAnimations = ResolveSelectedAnimations( castFiles, _baseCast );
		_useAnimationFolder = _selectedAnimations.Count == 0;

		WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.WindowTitle | WindowFlags.CloseButton | WindowFlags.WindowSystemMenuHint;
		DeleteOnClose = true;
		WindowTitle = _baseCast is not null
			? $"Create Animation Rigset from {Path.GetFileName( _baseCast.AbsolutePath )}"
			: "Create Animation Rigset";
		SetWindowIcon( "animation" );

		Layout = Layout.Column();
		Layout.Margin = 16;
		Layout.Spacing = 8;

		if ( _baseCast is not null )
			Layout.Add( new Label( $"Base CAST: {Path.GetFileName( _baseCast.AbsolutePath )}" ) { Color = Theme.TextControl.WithAlpha( 0.8f ) } );

		Layout.Add( new Label( $"Animation CASTs: {(_selectedAnimations.Count > 0 ? _selectedAnimations.Count.ToString() : "folder")}" ) { Color = Theme.TextControl.WithAlpha( 0.7f ) } );
		Layout.AddSpacingCell( 4 );

		_fileEdit = new LineEdit( this );
		_fileEdit.Text = GuessRigsetOutputPath( _baseCast, _selectedAnimations );
		_fileEdit.AddOptionToEnd( new Option( "Browse", "folder", BrowseFile ) );
		AddRow( "Save To", _fileEdit );

		_animationFolderEdit = new FolderEdit( this );
		_animationFolderEdit.Text = GuessAnimationFolder( _baseCast );
		_animationFolderRow = AddRow( "Anim Folder", _animationFolderEdit );
		_animationFolderRow.Visible = _useAnimationFolder;

		Layout.AddSpacingCell( 4 );
		Layout.Add( new Label( "Advanced Data" ) { Color = Theme.TextControl.WithAlpha( 0.85f ) } );

		AddRow( "Mode", _advancedDataCombo = new ComboBox( this ) );
		_advancedDataCombo.AddItem( "Basic only", icon: "looks_one" );
		_advancedDataCombo.AddItem( "Advanced when supported", icon: "auto_fix_high" );
		_advancedDataCombo.AddItem( "Strict advanced", icon: "verified" );
		_advancedDataCombo.CurrentIndex = 0;
		_advancedDataCombo.ItemChanged += UpdateAdvancedControls;

		_rootMotionModeRow = AddRow( "Root Motion", _rootMotionModeCombo = new ComboBox( this ) );
		_rootMotionModeCombo.AddItem( "Auto", icon: "travel_explore" );
		_rootMotionModeCombo.AddItem( "None", icon: "block" );
		_rootMotionModeCombo.AddItem( "Bone", icon: "account_tree" );
		_rootMotionModeCombo.CurrentIndex = 0;
		_rootMotionModeCombo.ItemChanged += UpdateAdvancedControls;

		_rootMotionBoneRow = AddRow( "Motion Bone", _rootMotionBoneCombo = new ComboBox( this ) );
		_rootMotionBoneCombo.Editable = true;
		PopulateRootMotionBones();
		UpdateAdvancedControls();

		var footer = Layout.AddRow();
		footer.Margin = new Sandbox.UI.Margin( 0, 8, 0, 0 );
		footer.AddStretchCell();

		var cancelButton = new Button( "Cancel", "close" );
		cancelButton.Clicked = Close;
		footer.Add( cancelButton );

		footer.AddSpacingCell( 8 );

		var createButton = new Button.Primary( "Create", "animation" );
		createButton.Clicked = OnCreate;
		footer.Add( createButton );

		FixedWidth = 460;
		AdjustSize();
		FixedHeight = Height;

		var geo = EditorCookie.GetString( "CreateAnimationRigsetFromCastDialog.Geometry", null );
		if ( geo is not null )
		{
			RestoreGeometry( geo );
		}
		else
		{
			Position = Application.CursorPosition - new Vector2( Width * 0.5f, 3 );
			ConstrainToScreen();
		}

		Show();
		Focus();
	}

	public CreateAnimationRigsetFromCastDialog( DirectoryInfo folder )
		: this( ResolveCastAssetsFromFolder( folder ) )
	{
		if ( folder is not null && _useAnimationFolder )
			_animationFolderEdit.Text = folder.FullName;
	}

	protected override void OnClosed()
	{
		EditorCookie.SetString( "CreateAnimationRigsetFromCastDialog.Geometry", SaveGeometry() );
		base.OnClosed();
	}

	Widget AddRow( string label, Widget control )
	{
		var row = new Widget( this );
		row.Layout = Layout.Row();
		row.Layout.Spacing = 8;
		row.Layout.Add( new Label( label ) { MinimumWidth = 90 } );
		row.Layout.Add( control, 1 );
		Layout.Add( row );
		return row;
	}

	void BrowseFile()
	{
		var result = EditorUtility.SaveFileDialog( "Save Animation Rigset As..", "rigset", _fileEdit.Text );
		if ( result is not null )
			_fileEdit.Text = result;
	}

	void OnCreate()
	{
		Close();

		if ( _baseCast is null )
		{
			Log.Warning( "Could not determine a base CAST file for animation rigset creation." );
			return;
		}

		if ( string.IsNullOrWhiteSpace( _fileEdit.Text ) )
			return;

		var baseSummary = EditorUtility.InspectCastFile( _baseCast.AbsolutePath );
		var animationFiles = ResolveAnimationFiles();
		if ( !baseSummary.HasAnimation && animationFiles.Count == 0 )
		{
			Log.Warning( $"No CAST animation files were found for base file \"{Path.GetFileName( _baseCast.AbsolutePath )}\"." );
			return;
		}

		var animationFolder = _useAnimationFolder ? _animationFolderEdit.Text : string.Empty;
		EditorUtility.CreateAnimationRigsetFromCastFiles( _baseCast, animationFiles, animationFolder, _fileEdit.Text, SelectedImportOptions );
	}

	void PopulateRootMotionBones()
	{
		string[] boneNames = _baseCast is not null
			? EditorUtility.InspectCastSkeletonBoneNames( _baseCast.AbsolutePath )
			: [];

		foreach ( var boneName in boneNames )
			_rootMotionBoneCombo.AddItem( boneName );

		var preferred = boneNames.FirstOrDefault( x => string.Equals( x, "root", StringComparison.OrdinalIgnoreCase ) ) ??
			boneNames.FirstOrDefault( x => string.Equals( x, "pelvis", StringComparison.OrdinalIgnoreCase ) ) ??
			boneNames.FirstOrDefault( x => string.Equals( x, "hips", StringComparison.OrdinalIgnoreCase ) );

		if ( !string.IsNullOrWhiteSpace( preferred ) )
			_rootMotionBoneCombo.CurrentText = preferred;
	}

	void UpdateAdvancedControls()
	{
		var advanced = SelectedAdvancedDataMode != CastAdvancedDataMode.BasicOnly;
		var boneMode = SelectedRootMotionMode == CastRootMotionMode.Bone;

		_rootMotionModeRow.Visible = advanced;
		_rootMotionBoneRow.Visible = advanced && boneMode;
		AdjustSize();
		FixedHeight = Height;
	}

	CastAdvancedDataMode SelectedAdvancedDataMode => _advancedDataCombo.CurrentIndex switch
	{
		1 => CastAdvancedDataMode.AdvancedWhenSupported,
		2 => CastAdvancedDataMode.StrictAdvanced,
		_ => CastAdvancedDataMode.BasicOnly
	};

	CastRootMotionMode SelectedRootMotionMode => _rootMotionModeCombo.CurrentIndex switch
	{
		1 => CastRootMotionMode.None,
		2 => CastRootMotionMode.Bone,
		_ => CastRootMotionMode.Auto
	};

	CastAnimatedModelImportOptions SelectedImportOptions => new()
	{
		AdvancedDataMode = SelectedAdvancedDataMode,
		RootMotionMode = SelectedRootMotionMode,
		RootMotionBoneName = _rootMotionBoneCombo.CurrentText
	};

	List<Asset> ResolveAnimationFiles()
	{
		if ( !_useAnimationFolder )
			return _selectedAnimations;

		var folderPath = _animationFolderEdit.Text;
		if ( string.IsNullOrWhiteSpace( folderPath ) || !Directory.Exists( folderPath ) )
			return [];

		return ResolveCastAssetsFromFolder( new DirectoryInfo( folderPath ) )
			.Where( x => x != _baseCast && EditorUtility.InspectCastFile( x.AbsolutePath ).HasAnimation )
			.ToList();
	}

	static Asset ResolveBaseCast( IReadOnlyList<Asset> castFiles )
	{
		if ( castFiles is null || castFiles.Count == 0 )
			return null;

		var candidates = castFiles
			.Where( x => x is not null )
			.Select( x => new { Asset = x, Summary = EditorUtility.InspectCastFile( x.AbsolutePath ) } )
			.ToList();

		if ( candidates.Count == 0 )
			return null;

		return candidates
			.OrderByDescending( x => CreateAnimatedModelFromCastDialog.ScoreCastFile( x.Summary ) )
			.Select( x => x.Asset )
			.FirstOrDefault();
	}

	static List<Asset> ResolveSelectedAnimations( IReadOnlyList<Asset> castFiles, Asset baseCast )
	{
		var animations = new List<Asset>();
		if ( castFiles is null )
			return animations;

		foreach ( var asset in castFiles )
		{
			if ( asset is null || asset == baseCast )
				continue;

			if ( !EditorUtility.InspectCastFile( asset.AbsolutePath ).HasAnimation )
				continue;

			animations.Add( asset );
		}

		return animations;
	}

	static List<Asset> ResolveCastAssetsFromFolder( DirectoryInfo folder )
	{
		if ( folder is null || !folder.Exists )
			return [];

		var files = new List<Asset>();
		foreach ( var filePath in Directory.GetFiles( folder.FullName, "*.cast", SearchOption.TopDirectoryOnly ).OrderBy( x => x, StringComparer.OrdinalIgnoreCase ) )
		{
			var asset = AssetSystem.FindByPath( filePath ) ?? AssetSystem.RegisterFile( filePath );
			if ( asset is not null )
				files.Add( asset );
		}

		return files;
	}

	static string GuessAnimationFolder( Asset baseCast )
	{
		if ( baseCast is null || string.IsNullOrWhiteSpace( baseCast.AbsolutePath ) )
			return string.Empty;

		var baseDirectory = Path.GetDirectoryName( baseCast.AbsolutePath );
		var guess = Path.Combine( baseDirectory ?? string.Empty, $"anims_{Path.GetFileNameWithoutExtension( baseCast.AbsolutePath )}" );
		return Directory.Exists( guess ) ? guess : baseDirectory;
	}

	static string GuessRigsetOutputPath( Asset baseCast, IReadOnlyList<Asset> selectedAnimations )
	{
		var animationDirectory = selectedAnimations?.FirstOrDefault()?.AbsolutePath is { } animationPath
			? Path.GetDirectoryName( animationPath )
			: GuessAnimationFolder( baseCast );

		if ( string.IsNullOrWhiteSpace( animationDirectory ) )
			animationDirectory = baseCast is not null ? Path.GetDirectoryName( baseCast.AbsolutePath ) : Project.Current?.GetAssetsPath();

		if ( string.IsNullOrWhiteSpace( animationDirectory ) )
			return string.Empty;

		return Path.Combine( animationDirectory, $"{Path.GetFileName( animationDirectory )}.rigset" );
	}
}
