#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 家庭贴纸换装（贴纸游戏选择页里的「家庭贴纸换装」）。
///
/// 素材是 <c>assset/Paper Doll Family/</c> 那套「打印用纸偶」：一张纸上印着爸爸 / 妈妈 /
/// 儿子 / 女儿 / 宝宝五个娃娃和一整套共用的衣服（一家人的衣服互通，谁都能穿）。
/// 原始 PNG 是米色纸卡带剪裁框的整张贴纸，所以先用工具离线抠图，成品放在同目录的
/// <c>cut/</c> 子目录里。
///
/// 这个玩法的特点：**同时给五口人换装**。舞台按家庭合影来摆 —— 后排是大人（爸爸 / 妈妈），
/// 前排是孩子（儿子 / 女儿 / 宝宝），前排自然压住后排的腿。底部面板最上面一行
/// 「换装对象」用来切换现在给谁换，没在换的那几个会压暗一点。
///
/// 分类是 6 类：上衣 / 下装 / 连身 / 帽子 / 配饰 / 背景。
/// 连身（连衣裙 / 背带裤）出现时会自动收起上衣与下装图层，避免叠穿。
///
/// 玩法沿用 <see cref="StickerGame"/> 的手感：
/// <list type="bullet">
/// <item>上衣 / 下装 / 连身 / 帽子 / 背景 —— 单选；</item>
/// <item>配饰 —— 多选开关，并且可以拖动摆放：按住拖 = 搬动贴纸，长按 = 切换「在角色身前 / 身后」；</item>
/// <item>身上的衣服 / 帽子 —— 穿上后也能按住拖动微调位置，换装或点「重置」时位置复位。</item>
/// </list>
///
/// 渲染分层（绝对 z_index，数值大的画在上面）：背景(-10) &lt; 配饰·身后(-5) &lt; 后排娃娃(0~13)
/// &lt; 前排娃娃(40~53) &lt; 配饰·身前(60) &lt; 拖动中(90)。
/// 因为孩子在前排、且会挡住后排大人的腿，所以前排整组（含衣服）层号统一加 40，
/// 保证「前排的整只娃娃」都在「后排的所有图层」之上。
/// </summary>
public partial class FamilyDressUp : Control
{
	// ---------- 分层 ZIndex 常量（绝对层号，娃体那一层的基准值）----------
	private const int ZBackRowBase = 0;    // 后排（爸爸 / 妈妈）
	private const int ZFrontRowBase = 40;  // 前排（儿子 / 女儿 / 宝宝）
	private const int ZRelBottom = 10;     // 相对娃体：下装
	private const int ZRelTop = 11;        // 相对娃体：上衣
	private const int ZRelHat = 12;        // 相对娃体：帽子
	private const int ZRelDress = 13;      // 相对娃体：连身
	private const int ZPropBack = -5;      // 配饰·全员身后（夹在背景 -10 与后排娃娃 0 之间）
	private const int ZPropFront = 60;     // 配饰·全员身前（在前排连身 53 之上）
	private const int ZDragging = 90;      // 拖动中的贴纸临时提到最前，松手还原

	private const string AssetDir = "res://assset/Paper Doll Family/cut/";
	private const string DingPath = "res://sfx/ding.wav";

	/// <summary>底部面板高度。919 的舞台高 = 1280 - 此值，站位常量是按 720x908 校准的。</summary>
	private const float PanelHeight = 372f;

	// Toast 提示的顶边（画布坐标）。顶栏下沿 y=112，提示摆在这条空档里。
	private const float ToastTop = 118f;

	// ---------- 拖拽手感参数 ----------
	private const float DragThreshold = 12f;
	private const double LongPressSeconds = 0.45;
	private static readonly Color HighlightTint = new Color(1f, 0.92f, 0.45f);
	private static readonly Color InactiveDollTint = new Color(0.66f, 0.66f, 0.74f);

	// ---------- 五个娃娃 ----------
	// 站位数值由离线合成校准工具（tools/_f_calib.py）算出：Scale = 目标身高 / 纹理高，
	// Rel = 相对舞台中心的偏移（站位坐标 - 舞台中心，舞台中心取 (360, 490)）。
	private static readonly string[] DollKeys = { "dad", "mom", "son", "daughter", "baby" };
	private static readonly string[] DollNames = { "爸爸", "妈妈", "儿子", "女儿", "宝宝" };
	// 场景里每个娃娃的 5 个 Sprite2D 节点名前缀
	private static readonly string[] DollNodes = { "Dad", "Mom", "Son", "Daughter", "Baby" };
	private static readonly float[] DollScale = { 0.68362f, 0.69542f, 0.7478f, 0.81366f, 0.78189f };
	private static readonly Vector2[] DollRel =
	{
		new Vector2(-160f, 40f),
		new Vector2(160f, 57.5f),
		new Vector2(-232f, 227.5f),
		new Vector2(0f, 229f),
		new Vector2(228f, 260f),
	};

	private const int DollCount = 5;

	// ---------- 初始造型（开局和「重置」都回到这里）----------
	// 爸爸：条纹衬衫 + 牛仔裤 + 蓝鸭舌帽；妈妈：粉开衫 + 牛仔裙；
	// 儿子：连帽衫 + 棕短裤；女儿：粉花裙（连身）；宝宝：条纹T恤 + 草帽。
	private static readonly int[] InitialTop = { 2, 3, 1, 0, 4 };
	private static readonly int[] InitialBottom = { 1, 3, 2, 0, 0 };
	private static readonly int[] InitialDress = { 0, 0, 0, 2, 0 };
	private static readonly int[] InitialCap = { 1, 0, 0, 0, 2 };
	private const int InitialBg = 0; // 暖家

	// ---------- 场景节点 ----------
	private TextureRect _background = null!;
	private Node2D _character = null!;
	private readonly Sprite2D[] _dollSprite = new Sprite2D[DollCount];
	private readonly Sprite2D[] _bottomSprite = new Sprite2D[DollCount];
	private readonly Sprite2D[] _topSprite = new Sprite2D[DollCount];
	private readonly Sprite2D[] _hatSprite = new Sprite2D[DollCount];
	private readonly Sprite2D[] _dressSprite = new Sprite2D[DollCount];
	private Node2D _props = null!;
	private HBoxContainer _topBar = null!;
	private HBoxContainer _dollTabs = null!;
	private HBoxContainer _categoryTabs = null!;
	private ScrollContainer _itemScroll = null!;
	private HBoxContainer _itemStrip = null!;
	private Button _homeButton = null!;
	private Button _resetButton = null!;
	private Button _photoButton = null!;
	private AudioStreamPlayer _sfx = null!;
	private Label _toast = null!;

	// ---------- 部件数据模型 ----------
	/// <summary>一件穿戴。<c>File == null</c> 表示「原装」/「不穿」/「不戴」。</summary>
	private sealed class ClothItem
	{
		public string Label = "";
		public string? File;
	}

	private sealed class PropItem
	{
		public string Label = "";
		public string Tex = "";
		public Vector2 StagePos;
		public Vector2 HomePos;
		public float Scale = 1f;
		public int Depth = ZPropFront;
		public int HomeDepth = ZPropFront;
		public Sprite2D? Sprite;
		public bool On;
	}

	private sealed class BgItem
	{
		public string Label = "";
		public Color Top = Colors.White;
		public Color Bottom = Colors.White;
		public string Decor = "none";
	}

	// 四个分类的选项表（标签是共用的，位置/缩放按娃娃各查一张表，见 ClothFit）
	private readonly List<ClothItem> _tops = new()
	{
		new ClothItem { Label = "原装" },
		new ClothItem { Label = "连帽衫", File = "blue_hoodie.png" },
		new ClothItem { Label = "条纹衬衫", File = "blue_striped_shirt.png" },
		new ClothItem { Label = "粉开衫", File = "pink_cardigan.png" },
		new ClothItem { Label = "条纹T恤", File = "striped_tshirt.png" },
	};

	private readonly List<ClothItem> _bottoms = new()
	{
		new ClothItem { Label = "原装" },
		new ClothItem { Label = "牛仔裤", File = "blue_jeans.png" },
		new ClothItem { Label = "棕短裤", File = "brown_shorts.png" },
		new ClothItem { Label = "牛仔裙", File = "denim_skirt.png" },
	};

	private readonly List<ClothItem> _dresses = new()
	{
		new ClothItem { Label = "原装" },
		new ClothItem { Label = "黄背带裤", File = "yellow_overalls.png" },
		new ClothItem { Label = "粉花裙", File = "pink_floral_dress.png" },
		new ClothItem { Label = "黄花长裙", File = "yellow_floral_dress.png" },
	};

	private readonly List<ClothItem> _caps = new()
	{
		new ClothItem { Label = "不戴" },
		new ClothItem { Label = "蓝鸭舌帽", File = "blue_cap.png" },
		new ClothItem { Label = "草帽", File = "straw_hat.png" },
	};

	// ---------- 服饰贴合表（唯一的数值出处：tools/_f_constants.json）----------
	// key = "{娃娃}/{衣服文件}"，Offset 是「衣服图中心相对娃娃纹理中心」的偏移，
	// 单位是娃娃纹理像素；贴到舞台时 Scale 要再乘娃娃的 Scale，Offset 也一样。
	private static readonly Dictionary<string, (float Scale, Vector2 Offset)> ClothFit = new()
	{
		["dad/blue_hoodie.png"] = (1.30206f, new Vector2(-2.0f, -57.58f)),
		["dad/blue_striped_shirt.png"] = (1.18468f, new Vector2(-2.0f, -51.21f)),
		["dad/pink_cardigan.png"] = (1.12853f, new Vector2(-2.0f, -58.83f)),
		["dad/striped_tshirt.png"] = (1.45906f, new Vector2(-2.0f, -59.3f)),
		["dad/blue_jeans.png"] = (0.85494f, new Vector2(-2.0f, 139.66f)),
		["dad/brown_shorts.png"] = (0.68156f, new Vector2(-2.0f, 101.01f)),
		["dad/denim_skirt.png"] = (0.65403f, new Vector2(-2.0f, 106.74f)),
		["dad/yellow_overalls.png"] = (1.54789f, new Vector2(-2.0f, -39.98f)),
		["dad/pink_floral_dress.png"] = (1.47104f, new Vector2(-2.0f, -50.11f)),
		["dad/yellow_floral_dress.png"] = (1.17895f, new Vector2(-2.0f, -9.66f)),
		["dad/blue_cap.png"] = (0.84877f, new Vector2(-2.0f, -285.15f)),
		["dad/straw_hat.png"] = (0.77846f, new Vector2(-2.0f, -286.69f)),

		["mom/blue_hoodie.png"] = (0.94895f, new Vector2(0.5f, -7.23f)),
		["mom/blue_striped_shirt.png"] = (0.86341f, new Vector2(0.5f, -2.58f)),
		["mom/pink_cardigan.png"] = (0.82248f, new Vector2(0.5f, -8.14f)),
		["mom/striped_tshirt.png"] = (1.06338f, new Vector2(0.5f, -8.48f)),
		["mom/blue_jeans.png"] = (0.76308f, new Vector2(0.5f, 100.8f)),
		["mom/brown_shorts.png"] = (0.60833f, new Vector2(0.5f, 66.3f)),
		["mom/denim_skirt.png"] = (0.58376f, new Vector2(0.5f, 71.91f)),
		["mom/yellow_overalls.png"] = (1.36827f, new Vector2(0.5f, 16.38f)),
		["mom/pink_floral_dress.png"] = (1.30034f, new Vector2(0.5f, 7.43f)),
		["mom/yellow_floral_dress.png"] = (1.04214f, new Vector2(0.5f, 43.18f)),
		["mom/blue_cap.png"] = (0.99123f, new Vector2(0.5f, -249.72f)),
		["mom/straw_hat.png"] = (0.90911f, new Vector2(0.5f, -251.53f)),

		["son/blue_hoodie.png"] = (0.84388f, new Vector2(2.0f, 31.7f)),
		["son/blue_striped_shirt.png"] = (0.76781f, new Vector2(2.0f, 35.83f)),
		["son/pink_cardigan.png"] = (0.73142f, new Vector2(2.0f, 30.89f)),
		["son/striped_tshirt.png"] = (0.94564f, new Vector2(2.0f, 30.59f)),
		["son/blue_jeans.png"] = (0.47185f, new Vector2(2.0f, 119.0f)),
		["son/brown_shorts.png"] = (0.37616f, new Vector2(2.0f, 97.67f)),
		["son/denim_skirt.png"] = (0.36096f, new Vector2(2.0f, 100.01f)),
		["son/yellow_overalls.png"] = (0.94957f, new Vector2(2.0f, 41.56f)),
		["son/pink_floral_dress.png"] = (0.90243f, new Vector2(2.0f, 35.35f)),
		["son/yellow_floral_dress.png"] = (0.72324f, new Vector2(2.0f, 60.16f)),
		["son/blue_cap.png"] = (0.72413f, new Vector2(2.0f, -145.46f)),
		["son/straw_hat.png"] = (0.66414f, new Vector2(2.0f, -146.78f)),

		["daughter/blue_hoodie.png"] = (0.67999f, new Vector2(18.5f, 45.2f)),
		["daughter/blue_striped_shirt.png"] = (0.61869f, new Vector2(18.5f, 48.52f)),
		["daughter/pink_cardigan.png"] = (0.58937f, new Vector2(18.5f, 44.55f)),
		["daughter/striped_tshirt.png"] = (0.76198f, new Vector2(18.5f, 44.3f)),
		["daughter/blue_jeans.png"] = (0.4601f, new Vector2(18.5f, 107.19f)),
		["daughter/brown_shorts.png"] = (0.36679f, new Vector2(18.5f, 86.39f)),
		["daughter/denim_skirt.png"] = (0.35197f, new Vector2(18.5f, 88.74f)),
		["daughter/yellow_overalls.png"] = (0.78288f, new Vector2(18.5f, 54.16f)),
		["daughter/pink_floral_dress.png"] = (0.74401f, new Vector2(18.5f, 49.04f)),
		["daughter/yellow_floral_dress.png"] = (0.59628f, new Vector2(18.5f, 69.49f)),
		["daughter/blue_cap.png"] = (0.84877f, new Vector2(18.5f, -131.65f)),
		["daughter/straw_hat.png"] = (0.77846f, new Vector2(18.5f, -133.19f)),

		["baby/blue_hoodie.png"] = (0.55565f, new Vector2(3.5f, 23.86f)),
		["baby/blue_striped_shirt.png"] = (0.50556f, new Vector2(3.5f, 26.58f)),
		["baby/pink_cardigan.png"] = (0.4816f, new Vector2(3.5f, 23.33f)),
		["baby/striped_tshirt.png"] = (0.62265f, new Vector2(3.5f, 23.13f)),
		["baby/blue_jeans.png"] = (0.48362f, new Vector2(3.5f, 91.01f)),
		["baby/brown_shorts.png"] = (0.38554f, new Vector2(3.5f, 69.15f)),
		["baby/denim_skirt.png"] = (0.36997f, new Vector2(3.5f, 71.15f)),
		["baby/yellow_overalls.png"] = (0.64352f, new Vector2(3.5f, 31.31f)),
		["baby/pink_floral_dress.png"] = (0.61157f, new Vector2(3.5f, 27.09f)),
		["baby/yellow_floral_dress.png"] = (0.49014f, new Vector2(3.5f, 43.91f)),
		["baby/blue_cap.png"] = (0.6529f, new Vector2(3.5f, -98.92f)),
		["baby/straw_hat.png"] = (0.59882f, new Vector2(3.5f, -100.11f)),
	};

	// 配饰（舞台绝对坐标）。Depth：-5=全员身后，60=全员身前。
	private readonly List<PropItem> _propItems = new()
	{
		new() { Label = "玩偶兔", Tex = "stuffed_bunny.png", StagePos = new Vector2(352, 215), Scale = 0.62f },
		new() { Label = "全家福", Tex = "family_photo.png", StagePos = new Vector2(626, 178), Scale = 0.58f },
		new() { Label = "爱心", Tex = "heart.png", StagePos = new Vector2(86, 170), Scale = 0.78f },
		new() { Label = "沙滩球", Tex = "beach_ball.png", StagePos = new Vector2(62, 300), Scale = 0.82f },
		new() { Label = "积木", Tex = "building_blocks.png", StagePos = new Vector2(660, 330), Scale = 0.62f },
		new() { Label = "提篮", Tex = "wicker_basket.png", StagePos = new Vector2(676, 838), Scale = 0.72f, Depth = ZPropBack },
		new() { Label = "小汽车", Tex = "toy_car.png", StagePos = new Vector2(92, 866), Scale = 0.70f },
	};

	private readonly List<BgItem> _bgs = new()
	{
		new BgItem { Label = "暖家", Top = new Color("#7a5a3a"), Bottom = new Color("#ffe9c9"), Decor = "sun_low" },
		new BgItem { Label = "晴空", Top = new Color("#8fc4e8"), Bottom = new Color("#f7fbff"), Decor = "sun" },
		new BgItem { Label = "海边", Top = new Color("#2f8fb8"), Bottom = new Color("#d8f2f0"), Decor = "bubbles" },
		new BgItem { Label = "花园", Top = new Color("#79c48a"), Bottom = new Color("#fdf4e0"), Decor = "bubbles" },
	};

	// ---------- 运行时状态 ----------
	private readonly List<ImageTexture> _bgTextures = new();
	private readonly Dictionary<Button, int> _categoryIndex = new();
	private readonly Dictionary<Button, int> _dollIndex = new();

	private readonly List<BaseButton> _stripButtons = new();
	private readonly List<int> _stripIndices = new();

	private int _activeDoll;     // 0爸爸 1妈妈 2儿子 3女儿 4宝宝
	private int _activeCategory; // 0上衣 1下装 2连身 3帽子 4配饰 5背景
	// 每个娃娃各自的穿搭，互不影响
	private readonly int[] _topIndex = new int[DollCount];
	private readonly int[] _bottomIndex = new int[DollCount];
	private readonly int[] _dressIndex = new int[DollCount];
	private readonly int[] _capIndex = new int[DollCount];
	private int _bgIndex = InitialBg;

	private Vector2 _center;
	private Tween? _toastTween;
	private int _photoCounter;

	// ---------- 拖拽状态 ----------
	private PropItem? _pressProp;    // 当前被按住的配饰贴纸
	private Sprite2D? _pressWear;    // 当前被按住的穿戴物（衣服 / 帽子 / 连身），与 _pressProp 二选一
	private Vector2 _pressPos;
	private Vector2 _grabOffset;
	private bool _dragging;
	private bool _longPressFired;
	private int _pressToken;
	private int _wearZBeforeDrag;    // 穿戴物拖动前的层号，松手还原
	private float _wearScaleBeforeDrag = 1f; // 穿戴物拖动前的缩放，松手还原
	private Rect2 _stageRect;

	public override void _Ready()
	{
		foreach (var p in _propItems)
		{
			p.HomePos = p.StagePos;
			p.HomeDepth = p.Depth;
		}

		_background = GetNode<TextureRect>("Stage/Background");
		_character = GetNode<Node2D>("Stage/Character");
		for (int d = 0; d < DollCount; d++)
		{
			_dollSprite[d] = GetNode<Sprite2D>($"Stage/Character/{DollNodes[d]}Doll");
			_bottomSprite[d] = GetNode<Sprite2D>($"Stage/Character/{DollNodes[d]}Bottom");
			_topSprite[d] = GetNode<Sprite2D>($"Stage/Character/{DollNodes[d]}Top");
			_hatSprite[d] = GetNode<Sprite2D>($"Stage/Character/{DollNodes[d]}Hat");
			_dressSprite[d] = GetNode<Sprite2D>($"Stage/Character/{DollNodes[d]}Dress");
		}
		_props = GetNode<Node2D>("Stage/Character/Props");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_dollTabs = GetNode<HBoxContainer>("UI/BottomPanel/VBox/DollTabs");
		_categoryTabs = GetNode<HBoxContainer>("UI/BottomPanel/VBox/CategoryTabs");
		_itemScroll = GetNode<ScrollContainer>("UI/BottomPanel/VBox/ItemScroll");
		_itemStrip = GetNode<HBoxContainer>("UI/BottomPanel/VBox/ItemScroll/ItemStrip");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_resetButton = GetNode<Button>("UI/TopBar/ResetButton");
		_photoButton = GetNode<Button>("UI/TopBar/PhotoButton");
		_sfx = GetNode<AudioStreamPlayer>("SfxPlayer");
		_toast = GetNode<Label>("Toast");

		SetProcessInput(true);

		if (ResourceLoader.Exists(DingPath))
			_sfx.Stream = GD.Load<AudioStream>(DingPath);

		for (int i = 0; i < _bgs.Count; i++)
			_bgTextures.Add(MakeBackgroundTexture(_bgs[i]));

		BuildTheme();
		BuildUi();
		EnsurePropSprites();

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		LayoutStage();
		ApplyInitialLook();
		SelectDoll(0, silent: true);
		SelectCategory(0, silent: true);

		GetViewport().SizeChanged += LayoutStage;

		GD.Print($"[Family] ready. selftest = {SelftestFlag.Describe()}");

		if (SelftestFlag.Read() == SelftestFlag.TokenFamily)
			_ = RunSelfTestAsync();
		else
			_ = ShowHintAsync();
	}

	// ================= 布局 =================

	private void LayoutStage()
	{
		Vector2 size = Size.X > 0 && Size.Y > 0 ? Size : new Vector2(720, 1280);

		GetNode<Control>("Stage").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		GetNode<Control>("UI").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		float stageH = Mathf.Max(size.Y - PanelHeight, 400);
		_center = new Vector2(size.X * 0.5f, stageH * 0.54f);
		_character.Position = _center;

		_stageRect = new Rect2(0, 0, size.X, Mathf.Max(size.Y - PanelHeight, 200));

		for (int d = 0; d < DollCount; d++)
			ApplyLookFor(d);

		foreach (var p in _propItems)
		{
			if (p.Sprite == null || p == _pressProp)
				continue;
			p.Sprite.Position = p.StagePos - _center;
		}
	}

	/// <summary>第 d 个娃娃相对舞台中心的偏移。</summary>
	private static Vector2 DollRelAt(int d) => DollRel[d];

	// ================= 主题与 UI 构建 =================

	private void BuildTheme()
	{
		var ui = GetNode<Control>("UI");
		ui.Theme = GameArt.MakeUiTheme();
		ui.MouseFilter = MouseFilterEnum.Ignore;
		GetNode<Control>("Stage").MouseFilter = MouseFilterEnum.Ignore;
		_background.MouseFilter = MouseFilterEnum.Ignore;
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
	}

	private void StyleButton(Button b, Color bg, Color font, float radius = 18, Color? border = null)
		=> GameArt.StyleButton(b, bg, font, radius, border);

	private void BuildUi()
	{
		// ---- 顶栏：返回（左） 重置 拍照（右） ----
		_topBar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_topBar.OffsetLeft = 20;
		_topBar.OffsetTop = 20;
		_topBar.OffsetRight = -20;
		_topBar.OffsetBottom = 112;
		_topBar.AddThemeConstantOverride("separation", 16);

		StyleButton(_homeButton, new Color("#8f7bff"), Colors.White);
		_homeButton.CustomMinimumSize = new Vector2(150, 92);
		_homeButton.Text = "返回";
		_homeButton.Pressed += OnHomePressed;

		StyleButton(_resetButton, new Color("#ff8f6b"), Colors.White);
		_resetButton.CustomMinimumSize = new Vector2(170, 92);
		_resetButton.Text = "重置";
		_resetButton.Pressed += OnResetPressed;

		var spacer = new Control();
		spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_topBar.AddChild(spacer);
		_topBar.MoveChild(spacer, 2);

		StyleButton(_photoButton, new Color("#4fa8ff"), Colors.White);
		_photoButton.CustomMinimumSize = new Vector2(170, 92);
		_photoButton.Text = "拍照";
		_photoButton.Pressed += OnPhotoPressed;

		// ---- 底部面板 ----
		var panel = GetNode<PanelContainer>("UI/BottomPanel");
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		panel.OffsetTop = -PanelHeight;
		panel.OffsetLeft = 0;
		panel.OffsetRight = 0;
		panel.OffsetBottom = 0;
		var panelBox = GameArt.MakeBox(new Color(1, 1, 1, 0.96f), 26);
		panelBox.ContentMarginLeft = 16;
		panelBox.ContentMarginRight = 16;
		panelBox.ContentMarginTop = 10;
		panelBox.ContentMarginBottom = 18;
		panel.AddThemeStyleboxOverride("panel", panelBox);

		var vbox = GetNode<VBoxContainer>("UI/BottomPanel/VBox");
		vbox.AddThemeConstantOverride("separation", 12);

		// ---- 换装对象（这个玩法特有的：五口人都要换）----
		_dollTabs.AddThemeConstantOverride("separation", 10);
		var who = new Label
		{
			Text = "换装对象",
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		who.AddThemeFontSizeOverride("font_size", 26);
		who.AddThemeColorOverride("font_color", new Color("#44445a"));
		who.CustomMinimumSize = new Vector2(130, 0);
		_dollTabs.AddChild(who);
		for (int i = 0; i < DollCount; i++)
		{
			var b = new Button
			{
				Text = DollNames[i],
				CustomMinimumSize = new Vector2(0, 62),
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			};
			b.Pressed += () =>
			{
				PlayPop(b);
				SelectDoll(_dollIndex[b]);
			};
			_dollTabs.AddChild(b);
			_dollIndex[b] = i;
		}

		// ---- 分类标签（6 个：上衣 / 下装 / 连身 / 帽子 / 配饰 / 背景）----
		_categoryTabs.AddThemeConstantOverride("separation", 10);
		string[] cats = { "上衣", "下装", "连身", "帽子", "配饰", "背景" };
		for (int i = 0; i < cats.Length; i++)
		{
			var b = new Button
			{
				Text = cats[i],
				CustomMinimumSize = new Vector2(0, 68),
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			};
			b.Pressed += () =>
			{
				PlayPop(b);
				SelectCategory(_categoryIndex[b]);
			};
			_categoryTabs.AddChild(b);
			_categoryIndex[b] = i;
		}

		// ---- 物品栏（横向滚动） ----
		_itemScroll.CustomMinimumSize = new Vector2(0, 146);
		_itemStrip.AddThemeConstantOverride("separation", 16);

		// ---- Toast 提示 ----
		_toast.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
		_toast.GrowHorizontal = GrowDirection.Both;
		_toast.GrowVertical = GrowDirection.End;
		_toast.OffsetTop = ToastTop;
		_toast.OffsetBottom = ToastTop;
		_toast.HorizontalAlignment = HorizontalAlignment.Center;
		_toast.MouseFilter = MouseFilterEnum.Ignore;
		_toast.ZIndex = 100;
		_toast.AddThemeFontSizeOverride("font_size", 34);
		_toast.AddThemeColorOverride("font_color", Colors.White);
		_toast.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.65f));
		_toast.AddThemeConstantOverride("outline_size", 8);
		var toastBox = GameArt.MakeBox(new Color(0.12f, 0.12f, 0.16f, 0.82f), 20);
		toastBox.ContentMarginLeft = toastBox.ContentMarginRight = 22;
		toastBox.ContentMarginTop = toastBox.ContentMarginBottom = 10;
		_toast.AddThemeStyleboxOverride("normal", toastBox);
		_toast.Visible = false;
	}

	private void RefreshDollTabs()
	{
		foreach (var kv in _dollIndex)
		{
			bool active = kv.Value == _activeDoll;
			Color bg = active ? new Color("#6cb6ff") : new Color("#f0f0f5");
			Color font = active ? Colors.White : new Color("#44445a");
			StyleButton(kv.Key, bg, font, radius: 20, border: active ? new Color("#2f86d8") : null);
		}
	}

	private void RefreshCategoryTabs()
	{
		foreach (var kv in _categoryIndex)
		{
			bool active = kv.Value == _activeCategory;
			Color bg = active ? new Color("#ffb347") : new Color("#f0f0f5");
			Color font = active ? Colors.White : new Color("#44445a");
			StyleButton(kv.Key, bg, font, radius: 20, border: active ? new Color("#e08a00") : null);
		}
	}

	/// <summary>把没在换的娃娃整体压暗，一眼看出现在在给谁换装。</summary>
	private void RefreshDollFocus()
	{
		for (int d = 0; d < DollCount; d++)
		{
			Color m = d == _activeDoll ? Colors.White : InactiveDollTint;
			_dollSprite[d].Modulate = m;
			_bottomSprite[d].Modulate = m;
			_topSprite[d].Modulate = m;
			_hatSprite[d].Modulate = m;
			_dressSprite[d].Modulate = m;
		}
	}

	// ================= 物品栏 =================

	private void SelectDoll(int index, bool silent = false)
	{
		_activeDoll = Mathf.Clamp(index, 0, DollCount - 1);
		RefreshDollTabs();
		RefreshDollFocus();
		RefreshStripHighlight();
		if (!silent)
		{
			PlayDing();
			ShowToast($"现在给{DollNames[_activeDoll]}换装");
		}
	}

	private void SelectCategory(int index, bool silent = false)
	{
		_activeCategory = index;
		RefreshCategoryTabs();
		RebuildItemStrip();
		if (!silent) PlayDing();
	}

	private void RebuildItemStrip()
	{
		foreach (var child in _itemStrip.GetChildren())
		{
			_itemStrip.RemoveChild(child);
			child.QueueFree();
		}
		_stripButtons.Clear();
		_stripIndices.Clear();

		switch (_activeCategory)
		{
			case 0:
				BuildOptionStrip(_tops.Count, i => (_tops[i].Label, TexOf(_tops[i])), i => ApplyTop(i));
				break;
			case 1:
				BuildOptionStrip(_bottoms.Count, i => (_bottoms[i].Label, TexOf(_bottoms[i])), i => ApplyBottom(i));
				break;
			case 2:
				BuildOptionStrip(_dresses.Count, i => (_dresses[i].Label, TexOf(_dresses[i])), i => ApplyDress(i));
				break;
			case 3:
				BuildOptionStrip(_caps.Count, i => (_caps[i].Label, TexOf(_caps[i])), i => ApplyCap(i));
				break;
			case 4:
				BuildPropStrip();
				break;
			case 5:
				BuildBgStrip();
				break;
		}

		RefreshStripHighlight();
	}

	/// <summary>物品栏缩略图路径：取「当前换装对象」穿这件时的贴图（同一件衣服各娃娃的图是一样的）。</summary>
	private static string? TexOf(ClothItem it) => it.File is null ? null : AssetDir + it.File;

	private void AddStripButton(BaseButton b, int itemIndex)
	{
		_itemStrip.AddChild(b);
		_stripButtons.Add(b);
		_stripIndices.Add(itemIndex);
	}

	private void BuildOptionStrip(int count, System.Func<int, (string Label, string? Tex)> get, System.Action<int> apply)
	{
		for (int i = 0; i < count; i++)
		{
			var (label, tex) = get(i);
			BaseButton b;
			if (tex is null)
			{
				var btn = new Button
				{
					Text = label,
					CustomMinimumSize = new Vector2(132, 132),
				};
				StyleButton(btn, new Color("#f0f0f5"), new Color("#44445a"), radius: 22);
				btn.AddThemeFontSizeOverride("font_size", 30);
				b = btn;
			}
			else
			{
				b = MakeItemButton(label, tex);
			}

			int idx = i;
			b.Pressed += () =>
			{
				PlayPop(b);
				apply(idx);
			};
			AddStripButton(b, idx);
		}
	}

	private void BuildPropStrip()
	{
		for (int i = 0; i < _propItems.Count; i++)
		{
			var item = _propItems[i];
			var tb = MakeItemButton(item.Label, AssetDir + item.Tex);
			tb.Pressed += () =>
			{
				PlayPop(tb);
				ToggleProp(item);
			};
			AddStripButton(tb, i);
		}
	}

	private void BuildBgStrip()
	{
		for (int i = 0; i < _bgs.Count; i++)
		{
			var bg = _bgs[i];
			var b = new Button
			{
				Text = bg.Label,
				CustomMinimumSize = new Vector2(132, 132),
				ExpandIcon = true,
				IconAlignment = HorizontalAlignment.Center,
				VerticalIconAlignment = VerticalAlignment.Top,
			};
			var thumb = MakeBackgroundTexture(bg, 132, 84);
			b.Icon = thumb;
			b.AddThemeColorOverride("font_color", Colors.White);
			b.AddThemeColorOverride("font_hover_color", Colors.White);
			b.AddThemeColorOverride("font_pressed_color", Colors.White);
			b.AddThemeColorOverride("font_focus_color", Colors.White);
			b.AddThemeFontSizeOverride("font_size", 26);
			b.AddThemeStyleboxOverride("normal", GameArt.MakeBox(new Color(1, 1, 1, 0.2f), 22));
			b.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1, 1, 1, 0.32f), 22));
			b.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(1, 1, 1, 0.1f), 22));
			int idx = i;
			b.Pressed += () =>
			{
				PlayPop(b);
				ApplyBackground(idx);
			};
			AddStripButton(b, idx);
		}
	}

	/// <summary>大号物品预览按钮（贴纸缩略图 + 名称角标）</summary>
	private TextureButton MakeItemButton(string label, string texPath)
	{
		var tb = new TextureButton
		{
			CustomMinimumSize = new Vector2(132, 132),
			IgnoreTextureSize = true,
			StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
		};
		tb.TextureNormal = GD.Load<Texture2D>(texPath);
		var sb = new StyleBoxFlat
		{
			BgColor = new Color(1, 1, 1, 0.85f),
			CornerRadiusTopLeft = 22,
			CornerRadiusTopRight = 22,
			CornerRadiusBottomLeft = 22,
			CornerRadiusBottomRight = 22,
			BorderColor = new Color(0.75f, 0.75f, 0.85f),
			BorderWidthLeft = 3,
			BorderWidthRight = 3,
			BorderWidthTop = 3,
			BorderWidthBottom = 3,
			ContentMarginLeft = 8,
			ContentMarginRight = 8,
			ContentMarginTop = 8,
			ContentMarginBottom = 8,
		};
		tb.AddThemeStyleboxOverride("normal", sb);
		tb.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1, 1, 1, 0.95f), 22));
		tb.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(0.92f, 0.92f, 1.0f), 22));
		tb.AddThemeStyleboxOverride("focus", GameArt.MakeBox(new Color(0, 0, 0, 0f), 22));

		var lb = new Label
		{
			Text = label,
			MouseFilter = MouseFilterEnum.Ignore,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		lb.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		lb.OffsetTop = -36;
		lb.OffsetBottom = -4;
		lb.AddThemeFontSizeOverride("font_size", 20);
		lb.AddThemeColorOverride("font_color", Colors.White);
		lb.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f));
		lb.AddThemeConstantOverride("outline_size", 8);
		tb.AddChild(lb);
		return tb;
	}

	private void RefreshStripHighlight()
	{
		int d = _activeDoll;
		for (int k = 0; k < _stripButtons.Count; k++)
		{
			int idx = _stripIndices[k];
			bool active = _activeCategory switch
			{
				0 => idx == _topIndex[d],
				1 => idx == _bottomIndex[d],
				2 => idx == _dressIndex[d],
				3 => idx == _capIndex[d],
				4 => idx >= 0 && idx < _propItems.Count && _propItems[idx].On,
				5 => idx == _bgIndex,
				_ => false,
			};
			_stripButtons[k].Modulate = active ? HighlightTint : Colors.White;
		}
	}

	// ================= 换装逻辑 =================

	private void ApplyTop(int index, bool silent = false)
	{
		_topIndex[_activeDoll] = index;
		if (index > 0) _dressIndex[_activeDoll] = 0; // 穿上衣就自动脱掉连身
		ApplyLookFor(_activeDoll);
		if (!silent) { PlayDing(); RefreshStripHighlight(); }
	}

	private void ApplyBottom(int index, bool silent = false)
	{
		_bottomIndex[_activeDoll] = index;
		if (index > 0) _dressIndex[_activeDoll] = 0; // 穿下装就自动脱掉连身
		ApplyLookFor(_activeDoll);
		if (!silent) { PlayDing(); RefreshStripHighlight(); }
	}

	private void ApplyDress(int index, bool silent = false)
	{
		_dressIndex[_activeDoll] = index;
		ApplyLookFor(_activeDoll);
		if (!silent) { PlayDing(); RefreshStripHighlight(); }
	}

	private void ApplyCap(int index, bool silent = false)
	{
		_capIndex[_activeDoll] = index;
		ApplyLookFor(_activeDoll);
		if (!silent) { PlayDing(); RefreshStripHighlight(); }
	}

	/// <summary>
	/// 把第 d 个娃娃的整套造型刷到舞台上。
	/// 连身（连衣裙 / 背带裤）出现时，上衣与下装图层整层收起，避免叠穿。
	/// </summary>
	private void ApplyLookFor(int d)
	{
		_dollSprite[d].Texture = GD.Load<Texture2D>(AssetDir + DollKeys[d] + "_doll.png");
		_dollSprite[d].Scale = Vector2.One * DollScale[d];
		_dollSprite[d].Position = DollRelAt(d);

		SetWear(_hatSprite[d], _caps[_capIndex[d]], d);

		if (_dressIndex[d] > 0)
		{
			SetWear(_dressSprite[d], _dresses[_dressIndex[d]], d);
			_topSprite[d].Texture = null;
			_bottomSprite[d].Texture = null;
		}
		else
		{
			_dressSprite[d].Texture = null;
			SetWear(_topSprite[d], _tops[_topIndex[d]], d);
			SetWear(_bottomSprite[d], _bottoms[_bottomIndex[d]], d);
		}
	}

	/// <summary>把一件穿戴贴到第 d 个娃娃身上（File 为 null 则清空该图层）。</summary>
	private void SetWear(Sprite2D target, ClothItem it, int d)
	{
		if (it.File is null)
		{
			target.Texture = null;
			return;
		}
		var fit = ClothFit[DollKeys[d] + "/" + it.File];
		target.Texture = GD.Load<Texture2D>(AssetDir + it.File);
		target.Scale = Vector2.One * fit.Scale * DollScale[d];
		PlaceWear(target, DollRelAt(d) + fit.Offset * DollScale[d]);
	}

	/// <summary>把一件穿戴物摆到基准位置，并记下这个基准，供「换装 / 重置」复位。</summary>
	private static void PlaceWear(Sprite2D s, Vector2 basePos)
	{
		s.Position = basePos;
		s.SetMeta("wear_home", basePos);
	}

	/// <summary>
	/// 五个娃娃全部可拖动的穿戴物（连身 / 帽子 / 上衣 / 下装）。
	/// 按「实际画在最上面」的顺序枚举：前排（儿子 / 女儿 / 宝宝，层号 50~53）先于后排，
	/// 每个娃娃内部又是 连身 &gt; 帽子 &gt; 上衣 &gt; 下装，这样重叠处抓到的是肉眼看到的那一件。
	/// </summary>
	private IEnumerable<Sprite2D> WearSprites()
	{
		for (int d = DollCount - 1; d >= 0; d--)
		{
			yield return _dressSprite[d];
			yield return _hatSprite[d];
			yield return _topSprite[d];
			yield return _bottomSprite[d];
		}
	}

	/// <summary>找出这个坐标点下穿在身上的那件衣服（这一格没穿东西就不算）。</summary>
	private Sprite2D? PickWear(Vector2 canvasPos)
	{
		foreach (var s in WearSprites())
		{
			if (s.Texture != null && HitSprite(s, canvasPos))
				return s;
		}
		return null;
	}

	/// <summary>Sprite2D.GetRect() 不含节点缩放，乘上 Scale 才是屏幕上真正能点到的范围。</summary>
	private static bool HitSprite(Sprite2D s, Vector2 canvasPos)
	{
		Vector2 size = s.GetRect().Size * s.Scale;
		return new Rect2(-size / 2f, size).HasPoint(canvasPos - s.GlobalPosition);
	}

	private void ApplyBackground(int index, bool silent = false)
	{
		_bgIndex = index;
		_background.Texture = _bgTextures[index];
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ToggleProp(PropItem p)
	{
		p.On = !p.On;
		if (p.Sprite != null)
			p.Sprite.Visible = p.On;
		PlayDing();
		RefreshStripHighlight();
	}

	private void EnsurePropSprites()
	{
		foreach (var p in _propItems)
		{
			var s = new Sprite2D
			{
				Texture = GD.Load<Texture2D>(AssetDir + p.Tex),
				Scale = Vector2.One * p.Scale,
				Visible = false,
				ZAsRelative = false,
				ZIndex = p.Depth,
			};
			_props.AddChild(s);
			p.Sprite = s;
		}
	}

	private void OnResetPressed()
	{
		PlayPop(_resetButton);
		ApplyInitialLook(silent: false);
		ShowToast("已恢复初始造型");
	}

	private void OnHomePressed()
	{
		PlayPop(_homeButton);
		GetTree().ChangeSceneToFile(ScenePaths.StickerSelect);
	}

	/// <summary>回到初始造型：五口人各自穿好，配饰全收起并归位，背景回到暖家。</summary>
	private void ApplyInitialLook(bool silent = true)
	{
		for (int d = 0; d < DollCount; d++)
		{
			_topIndex[d] = InitialTop[d];
			_bottomIndex[d] = InitialBottom[d];
			_dressIndex[d] = InitialDress[d];
			_capIndex[d] = InitialCap[d];
			ApplyLookFor(d);
		}
		ApplyBackground(InitialBg, silent: true);

		foreach (var p in _propItems)
		{
			p.On = false;
			p.StagePos = p.HomePos;
			p.Depth = p.HomeDepth;
			if (p.Sprite != null)
			{
				p.Sprite.Visible = false;
				p.Sprite.ZIndex = p.Depth;
				p.Sprite.Scale = Vector2.One * p.Scale;
				p.Sprite.Position = p.StagePos - _center;
			}
		}

		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	// ================= 拖拽贴纸 =================

	public override void _Input(InputEvent @event)
	{
		bool consumed = false;
		switch (@event)
		{
			case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
				consumed = mb.Pressed ? BeginPress(mb.Position) : EndPress();
				break;
			case InputEventMouseMotion mm:
				consumed = MovePress(mm.Position);
				break;
			case InputEventScreenTouch st:
				consumed = st.Pressed ? BeginPress(st.Position) : EndPress();
				break;
			case InputEventScreenDrag sd:
				consumed = MovePress(sd.Position);
				break;
		}

		if (consumed)
			GetViewport().SetInputAsHandled();
	}

	private bool BeginPress(Vector2 canvasPos)
	{
		if (_pressProp != null || _pressWear != null)
			return false;
		if (!_stageRect.HasPoint(canvasPos))
			return false;

		_pressPos = canvasPos;
		_dragging = false;
		_longPressFired = false;

		// 配饰优先：它原本就有「长按翻层」的手势，先按旧规则判它
		var hit = PickProp(canvasPos);
		if (hit?.Sprite != null)
		{
			_pressProp = hit;
			_grabOffset = canvasPos - hit.Sprite.GlobalPosition;
			ArmLongPress(++_pressToken, hit);
			return true;
		}

		// 没点到配饰，再试试身上那件衣服（穿上后才能拖动微调）
		var wear = PickWear(canvasPos);
		if (wear == null)
			return false;

		_pressWear = wear;
		_grabOffset = canvasPos - wear.GlobalPosition;
		_pressToken++; // 作废上一轮还在等待的长按计时
		return true;
	}

	private bool MovePress(Vector2 canvasPos)
	{
		var prop = _pressProp;
		var wear = _pressWear;
		if (prop?.Sprite == null && wear == null)
			return false;
		if (_longPressFired)
			return true;

		if (!_dragging)
		{
			if (canvasPos.DistanceTo(_pressPos) < DragThreshold)
				return true;
			_dragging = true;
			_pressToken++;
			if (prop?.Sprite != null)
			{
				prop.Sprite.ZIndex = ZDragging;
				prop.Sprite.Scale = Vector2.One * prop.Scale * 1.06f;
			}
			else if (wear != null)
			{
				_wearZBeforeDrag = wear.ZIndex;
				_wearScaleBeforeDrag = wear.Scale.X;
				wear.ZIndex = ZDragging;
				wear.Scale = Vector2.One * _wearScaleBeforeDrag * 1.06f;
			}
		}

		Vector2 pos = canvasPos - _grabOffset;
		pos.X = Mathf.Clamp(pos.X, 0f, Size.X);
		pos.Y = Mathf.Clamp(pos.Y, 0f, _stageRect.Size.Y);
		if (prop?.Sprite != null)
			prop.Sprite.GlobalPosition = pos;
		else if (wear != null)
			wear.GlobalPosition = pos;
		return true;
	}

	private bool EndPress()
	{
		var prop = _pressProp;
		var wear = _pressWear;
		if (prop == null && wear == null)
			return false;

		_pressProp = null;
		_pressWear = null;
		_pressToken++;

		if (_dragging)
		{
			if (prop?.Sprite != null)
			{
				prop.StagePos = prop.Sprite.GlobalPosition;
				prop.Sprite.Scale = Vector2.One * prop.Scale;
				prop.Sprite.ZIndex = prop.Depth;
			}
			else if (wear != null)
			{
				// 衣服的位置直接留在节点上；下次换装 / 重置时 SetWear 会按基准重新摆好
				wear.Scale = Vector2.One * _wearScaleBeforeDrag;
				wear.ZIndex = _wearZBeforeDrag;
			}
		}

		bool wasDragging = _dragging;
		_dragging = false;
		_longPressFired = false;
		return wasDragging;
	}

	private async void ArmLongPress(int token, PropItem p)
	{
		await ToSignal(GetTree().CreateTimer(LongPressSeconds), SceneTreeTimer.SignalName.Timeout);
		if (token != _pressToken || _pressProp != p || _dragging)
			return;
		_longPressFired = true;
		TogglePropDepth(p);
	}

	private void TogglePropDepth(PropItem p)
	{
		p.Depth = p.Depth == ZPropBack ? ZPropFront : ZPropBack;
		if (p.Sprite != null)
			p.Sprite.ZIndex = p.Depth;
		PlayDing();
		ShowToast(p.Depth == ZPropFront ? $"{p.Label}：移到角色前面" : $"{p.Label}：移到角色后面");
	}

	private PropItem? PickProp(Vector2 canvasPos)
	{
		PropItem? best = null;
		int bestZ = int.MinValue;
		foreach (var p in _propItems)
		{
			if (!p.On || p.Sprite == null)
				continue;
			if (!p.Sprite.GetRect().HasPoint(canvasPos - p.Sprite.GlobalPosition))
				continue;
			if (p.Sprite.ZIndex >= bestZ)
			{
				bestZ = p.Sprite.ZIndex;
				best = p;
			}
		}
		return best;
	}

	// ================= 拍照 =================

	private async void OnPhotoPressed()
	{
		PlayPop(_photoButton);
		PlayDing();

		var vp = GetViewport();
		bool topBarWasVisible = _topBar.Visible;
		bool toastWasVisible = _toast.Visible;
		_topBar.Visible = false;
		_toast.Visible = false;
		for (int d = 0; d < DollCount; d++)
		{
			_dollSprite[d].Modulate = Colors.White;
			_bottomSprite[d].Modulate = Colors.White;
			_topSprite[d].Modulate = Colors.White;
			_hatSprite[d].Modulate = Colors.White;
			_dressSprite[d].Modulate = Colors.White;
		}

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		var img = vp.GetTexture().GetImage();

		_topBar.Visible = topBarWasVisible;
		_toast.Visible = toastWasVisible;
		RefreshDollFocus();

		float bottom = Size.Y - PanelHeight;
		var canvasRect = new Rect2(0, 0, Size.X, bottom);
		var xf = vp.GetScreenTransform();
		Vector2 pxPos = xf * canvasRect.Position;
		Vector2 pxSize = xf.BasisXform(canvasRect.Size);
		var region = new Rect2I(
			(Vector2I)(pxPos + new Vector2(0.5f, 0.5f)),
			(Vector2I)(pxSize - new Vector2(1, 1)));
		region = region.Intersection(new Rect2I(Vector2I.Zero, img.GetSize()));
		GD.Print($"[Photo] img={img.GetSize()} region={region}");
		if (region.Size.X <= 8 || region.Size.Y <= 8)
		{
			GD.PushError($"[Photo] invalid capture region {region}");
			ShowToast("拍照失败");
			return;
		}

		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		_photoCounter++;
		string path = $"user://screenshots/family_{stamp}_{_photoCounter}.png";
		var photo = img.GetRegion(region);
		var err = photo.SavePng(path);
		if (err == Error.Ok)
		{
			GD.Print($"[Photo] saved#{_photoCounter}: {ProjectSettings.GlobalizePath(path)}");
			ShowToast("咔嚓！照片已保存");
		}
		else
		{
			GD.PushError($"[Photo] save failed: {err}");
			ShowToast("保存失败");
		}
	}

	// ================= 音效与动效 =================

	private void PlayDing()
	{
		if (_sfx.Stream != null)
			_sfx.Play();
	}

	private void PlayPop(Node node)
	{
		var b = node as Control;
		if (b == null) return;
		b.PivotOffset = b.Size * 0.5f;
		var tw = CreateTween();
		tw.TweenProperty(b, "scale", new Vector2(0.9f, 0.9f), 0.06);
		tw.TweenProperty(b, "scale", Vector2.One, 0.16)
			.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
	}

	private void ShowToast(string msg)
	{
		_toastTween?.Kill();
		_toast.Text = msg;
		_toast.Visible = true;
		_toast.Modulate = Colors.White;
		_toastTween = CreateTween();
		_toastTween.TweenInterval(1.6);
		_toastTween.TweenProperty(_toast, "modulate:a", 0f, 0.4);
		_toastTween.TweenCallback(Callable.From(() => _toast.Visible = false));
	}

	private async Task ShowHintAsync()
	{
		await Wait(1.0);
		ShowToast("先选家人 · 按住拖动贴纸 · 长按翻层");
	}

	// ================= 程序化背景生成 =================
	//
	// Image.FillRect 是「直接写像素」，不做 alpha 混合：写进去的 alpha != 1 就会留下半透明窟窿。
	// 想画半透明的东西必须自己先算好混合结果，见 FillRow 里的强制 a = 1。

	private ImageTexture MakeBackgroundTexture(BgItem bg, int w = 720, int h = 1280)
	{
		var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
		for (int y = 0; y < h; y++)
			FillRow(img, y, 0, w - 1, SkyAt(bg, y, h));

		switch (bg.Decor)
		{
			case "sun":
				DrawGlowCircle(img, bg, h, (int)(w * 0.76f), (int)(h * 0.10f), (int)(w * 0.11f), new Color(1f, 0.94f, 0.62f));
				break;
			case "sun_low":
				DrawGlowCircle(img, bg, h, (int)(w * 0.22f), (int)(h * 0.16f), (int)(w * 0.15f), new Color(1f, 0.86f, 0.52f));
				break;
			case "bubbles":
				DrawGlowCircle(img, bg, h, (int)(w * 0.18f), (int)(h * 0.13f), (int)(w * 0.05f), Colors.White);
				DrawGlowCircle(img, bg, h, (int)(w * 0.74f), (int)(h * 0.24f), (int)(w * 0.04f), Colors.White);
				DrawGlowCircle(img, bg, h, (int)(w * 0.80f), (int)(h * 0.40f), (int)(w * 0.06f), Colors.White);
				break;
		}
		return ImageTexture.CreateFromImage(img);
	}

	private static Color SkyAt(BgItem bg, int y, int h)
	{
		float t = h > 1 ? y / (float)(h - 1) : 0f;
		return bg.Top.Lerp(bg.Bottom, t);
	}

	private static void FillRow(Image img, int y, int x0, int x1, Color c)
	{
		if (y < 0 || y >= img.GetHeight())
			return;
		x0 = Mathf.Max(x0, 0);
		x1 = Mathf.Min(x1, img.GetWidth() - 1);
		if (x1 < x0)
			return;
		img.FillRect(new Rect2I(x0, y, x1 - x0 + 1, 1), new Color(c.R, c.G, c.B, 1f));
	}

	private static void DrawGlowCircle(Image img, BgItem bg, int h, int cx, int cy, int r, Color core)
	{
		for (int dy = -r * 2; dy <= r * 2; dy++)
		{
			int y = cy + dy;
			if (y < 0 || y >= h)
				continue;
			float dist = Mathf.Abs(dy);
			float a = Mathf.Clamp(1f - dist / (2f * r), 0f, 1f);
			if (a <= 0f)
				continue;
			int half = (int)(r * Mathf.Sqrt(Mathf.Max(0f, 1f - (dist / (2f * r)) * (dist / (2f * r)) * 4f)));
			var c = SkyAt(bg, y, h).Lerp(core, a * 0.9f);
			FillRow(img, y, cx - half, cx + half, c);
		}
	}

	// ================= 自测模式 =================

	private async Task RunSelfTestAsync()
	{
		try
		{
			await RunSelfTestBodyAsync();
		}
		catch (System.Exception e)
		{
			GD.Print($"[SELFTEST] CRASHED: {e}");
			GetTree().Quit();
		}
	}

	private async Task RunSelfTestBodyAsync()
	{
		GD.Print("[SELFTEST] begin (family)");
		await Wait(0.4);

		int fails = 0;

		// ① 纹理加载：五个娃娃 + 四类穿戴 + 配饰
		for (int d = 0; d < DollCount; d++)
			if (_dollSprite[d].Texture == null) { GD.PushError($"[SELFTEST] doll#{d} texture null"); fails++; }
		foreach (var o in _tops)
			if (o.File != null && GD.Load<Texture2D>(TexOf(o)) == null) { GD.PushError($"[SELFTEST] top {o.Label} null"); fails++; }
		foreach (var o in _bottoms)
			if (o.File != null && GD.Load<Texture2D>(TexOf(o)) == null) { GD.PushError($"[SELFTEST] bottom {o.Label} null"); fails++; }
		foreach (var o in _dresses)
			if (o.File != null && GD.Load<Texture2D>(TexOf(o)) == null) { GD.PushError($"[SELFTEST] dress {o.Label} null"); fails++; }
		foreach (var o in _caps)
			if (o.File != null && GD.Load<Texture2D>(TexOf(o)) == null) { GD.PushError($"[SELFTEST] cap {o.Label} null"); fails++; }
		foreach (var p in _propItems)
			if (p.Sprite?.Texture == null) { GD.PushError($"[SELFTEST] prop {p.Label} texture null"); fails++; }
		// 每个娃娃 × 每件衣服都必须查得到贴合参数
		int missingFit = 0;
		foreach (var key in DollKeys)
			foreach (var o in _tops) if (o.File != null && !ClothFit.ContainsKey(key + "/" + o.File)) missingFit++;
		foreach (var key in DollKeys)
			foreach (var o in _bottoms) if (o.File != null && !ClothFit.ContainsKey(key + "/" + o.File)) missingFit++;
		foreach (var key in DollKeys)
			foreach (var o in _dresses) if (o.File != null && !ClothFit.ContainsKey(key + "/" + o.File)) missingFit++;
		foreach (var key in DollKeys)
			foreach (var o in _caps) if (o.File != null && !ClothFit.ContainsKey(key + "/" + o.File)) missingFit++;
		GD.Print($"[SELFTEST] textures: ok, missing fit entries = {missingFit}");
		if (missingFit > 0) fails++;

		// ② 背景完全不透明
		int translucent = 0;
		foreach (var tex in _bgTextures)
		{
			byte[] data = tex.GetImage().GetData();
			for (int i = 3; i < data.Length; i += 4)
				if (data[i] != 255)
					translucent++;
		}
		if (translucent > 0) { GD.PushError($"[SELFTEST] background has {translucent} translucent pixels"); fails++; }
		else GD.Print("[SELFTEST] background opaque: ok");

		// ③ 初始造型
		bool initialOk = _bgIndex == InitialBg;
		for (int d = 0; d < DollCount; d++)
			initialOk &= _topIndex[d] == InitialTop[d] && _bottomIndex[d] == InitialBottom[d] &&
						  _dressIndex[d] == InitialDress[d] && _capIndex[d] == InitialCap[d];
		GD.Print($"[SELFTEST] initial look: bg={_bgIndex} " +
				 $"tops=[{string.Join(",", _topIndex)}] bottoms=[{string.Join(",", _bottomIndex)}] " +
				 $"dresses=[{string.Join(",", _dressIndex)}] caps=[{string.Join(",", _capIndex)}] -> {initialOk}");
		if (!initialOk) fails++;

		// ④ 五个娃娃的穿搭互相独立
		int dadTopBefore = _topIndex[0];
		SelectDoll(4, silent: true); // 宝宝
		ApplyTop(1);
		await Wait(0.12);
		bool independent = _topIndex[4] == 1 && _topIndex[0] == dadTopBefore;
		GD.Print($"[SELFTEST] independent looks: baby top=1 dad top={_topIndex[0]} (expect {dadTopBefore}) -> {independent}");
		if (!independent) fails++;
		SelectDoll(0, silent: true);

		// ⑤ 连身出现时收起上衣 + 下装；切回上衣/下装时自动脱掉连身
		SelectDoll(3, silent: true); // 女儿（初始就是连身）
		ApplyDress(1);
		await Wait(0.12);
		bool dressHides = _dressSprite[3].Texture != null &&
						  _topSprite[3].Texture == null && _bottomSprite[3].Texture == null;
		GD.Print($"[SELFTEST] dress hides top/bottom: dress={_dressSprite[3].Texture != null} " +
				 $"top={_topSprite[3].Texture != null} bottom={_bottomSprite[3].Texture != null} -> {dressHides}");
		if (!dressHides) fails++;
		ApplyTop(1);
		await Wait(0.12);
		bool topClearsDress = _dressIndex[3] == 0 && _dressSprite[3].Texture == null && _topSprite[3].Texture != null;
		GD.Print($"[SELFTEST] top clears dress: dressIdx={_dressIndex[3]} dressTex={_dressSprite[3].Texture != null} -> {topClearsDress}");
		if (!topClearsDress) fails++;

		// ⑥ 逐分类、逐娃娃切换（把每一档走一遍）
		for (int d = 0; d < DollCount; d++)
		{
			SelectDoll(d, silent: true);
			for (int i = 0; i < _tops.Count; i++) { ApplyTop(i); await Wait(0.04); }
			for (int i = 0; i < _bottoms.Count; i++) { ApplyBottom(i); await Wait(0.04); }
			for (int i = 0; i < _dresses.Count; i++) { ApplyDress(i); await Wait(0.04); }
			for (int i = 0; i < _caps.Count; i++) { ApplyCap(i); await Wait(0.04); }
		}
		for (int i = 0; i < _bgs.Count; i++) { ApplyBackground(i); await Wait(0.06); }
		ApplyInitialLook();

		// ⑦ 「原装」档位的高亮也要跟着走
		SelectDoll(0, silent: true);
		SelectCategory(0, silent: true);
		ApplyTop(0);
		await Wait(0.1);
		bool stripOffOk = StripButtonLit(0);
		GD.Print($"[SELFTEST] \"原装\" button highlighted: {stripOffOk}");
		if (!stripOffOk) fails++;

		// ⑧ 换装对象那一行：五颗按钮都在、名字对
		bool dollTabsOk = _dollIndex.Count == DollCount;
		foreach (var kv in _dollIndex)
			if (kv.Key.Text != DollNames[kv.Value])
				dollTabsOk = false;
		GD.Print($"[SELFTEST] doll tabs: {_dollIndex.Count} buttons -> {dollTabsOk}");
		if (!dollTabsOk) fails++;

		// ⑨ 分类标签：六个都在
		bool catTabsOk = _categoryIndex.Count == 6;
		GD.Print($"[SELFTEST] category tabs: {_categoryIndex.Count} buttons -> {catTabsOk}");
		if (!catTabsOk) fails++;

		ApplyInitialLook();
		await Wait(0.2);

		// 身上的衣服也能拖动：抓住儿子的上衣 → 往下挪 → 松手，位置就地生效；重新换装要复位。
		// 此刻配饰还没全开，舞台上能点到的穿戴物只有五个娃娃的衣服，不会互相抢。
		var sonTopSprite = _topSprite[2];
		Vector2 sonTopHome = sonTopSprite.GlobalPosition;
		bool wearGrab = BeginPress(sonTopSprite.GlobalPosition);
		MovePress(sonTopSprite.GlobalPosition + new Vector2(36, 44));
		bool wearDrop = EndPress();
		float wearMoved = sonTopSprite.GlobalPosition.DistanceTo(sonTopHome);
		bool wearDragOk = wearGrab && wearDrop && wearMoved > 20f;
		GD.Print($"[SELFTEST] drag son top: grab={wearGrab} drop={wearDrop} moved={wearMoved:0.#}px -> {sonTopSprite.Position}");
		if (!wearDragOk) fails++;

		ApplyInitialLook();
		await Wait(0.15);
		bool wearResetOk = sonTopSprite.GlobalPosition.IsEqualApprox(sonTopHome);
		GD.Print($"[SELFTEST] re-dress son top resets position: {sonTopSprite.GlobalPosition} vs {sonTopHome} -> {wearResetOk}");
		if (!wearResetOk) fails++;

		// ⑩ 配饰全开
		foreach (var p in _propItems) { ToggleProp(p); await Wait(0.04); }
		await Wait(0.25);

		// ⑪ 拖拽
		var probe = _propItems[0];
		Vector2 startPos = probe.StagePos;
		bool grabOk = BeginPress(startPos);
		MovePress(startPos + new Vector2(10, 325));
		bool dropOk = EndPress();
		float movedBy = probe.StagePos.DistanceTo(startPos);
		bool dragOk = grabOk && dropOk && movedBy > 50f;
		GD.Print($"[SELFTEST] drag {probe.Label}: grab={grabOk} drop={dropOk} moved={movedBy:0.#}px -> {probe.StagePos}");
		if (!dragOk) fails++;

		// ⑫ 点空白处不应该抓到任何贴纸
		bool emptyMiss = BeginPress(new Vector2(Size.X - 4, 4)) == false;
		GD.Print($"[SELFTEST] tap on empty area grabs nothing: {emptyMiss}");
		if (!emptyMiss) fails++;

		// ⑬ 拍照
		OnPhotoPressed();
		await Wait(0.8);

		// ⑭ 长按 0.45 秒：切换身前 / 身后（只留探头这一张，避免别的贴纸抢命中）
		foreach (var p in _propItems)
			if (p != probe && p.On)
				ToggleProp(p);
		await Wait(0.15);
		int depthBefore = probe.Depth;
		bool lpGrab = BeginPress(probe.StagePos);
		await Wait(0.7);
		bool depthFlipped = probe.Depth != depthBefore && probe.Sprite != null && probe.Sprite.ZIndex == probe.Depth;
		EndPress();
		await Wait(0.1);
		GD.Print($"[SELFTEST] long press flips depth: grab={lpGrab} {depthBefore} -> {probe.Depth} ({depthFlipped})");
		if (!depthFlipped) fails++;

		// ⑮ 重置
		ApplyInitialLook(silent: false);
		await Wait(0.4);
		bool resetOk = probe.StagePos.IsEqualApprox(probe.HomePos) && probe.Depth == probe.HomeDepth && !probe.On && _bgIndex == InitialBg;
		for (int d = 0; d < DollCount; d++)
			resetOk &= _topIndex[d] == InitialTop[d] && _bottomIndex[d] == InitialBottom[d] &&
					   _dressIndex[d] == InitialDress[d] && _capIndex[d] == InitialCap[d];
		GD.Print($"[SELFTEST] after reset: bg={_bgIndex} probeHome={resetOk}");
		if (!resetOk) fails++;

		// ⑯ 验证截图文件
		string dir = ProjectSettings.GlobalizePath("user://screenshots");
		bool found = System.IO.Directory.Exists(dir) &&
					 System.IO.Directory.GetFiles(dir, "family_*.png").Length > 0;
		GD.Print($"[SELFTEST] photo exists: {found}");
		if (!found) fails++;

		// ⑰ 顶栏
		bool topBarOk = _homeButton.Visible && _resetButton.Visible && _photoButton.Visible &&
						_homeButton.Text == "返回" && _resetButton.Text == "重置" && _photoButton.Text == "拍照" &&
						_homeButton.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0;
		GD.Print($"[SELFTEST] top bar: \"{_homeButton.Text}\" / \"{_resetButton.Text}\" / \"{_photoButton.Text}\" -> {topBarOk}");
		if (!topBarOk) fails++;

		SaveShot("family");

		bool passed = fails == 0;
		GD.Print(passed ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} problem(s))");

		// 最后一项：点「返回」要真的回到贴纸游戏选择页。
		// 换场景会连同这里的协程一起销毁，所以校验交给挂树根的见证节点；这行之后不能再 await。
		var witness = new BackWitness(passed);
		GetTree().Root.AddChild(witness);
		_ = witness.VerifyAsync();
		_homeButton.EmitSignal(BaseButton.SignalName.Pressed);
	}

	/// <summary>整屏截图（自测用）。</summary>
	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Family] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	/// <summary>换场景之后的见证者（详见 <see cref="WinterDressUp"/> 里的同名实现）。</summary>
	private sealed partial class BackWitness : Node
	{
		private readonly bool _passedBefore;
		private readonly List<string> _rootScenes = new();

		public BackWitness(bool passedBefore)
		{
			_passedBefore = passedBefore;
		}

		public override void _Ready()
		{
			GetTree().NodeAdded += OnNodeAdded;
		}

		private void OnNodeAdded(Node node)
		{
			if (node.GetParent() == GetTree().Root && node is not BackWitness)
				_rootScenes.Add(node.Name);
		}

		public async Task VerifyAsync()
		{
			await ToSignal(GetTree().CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);
			GetTree().NodeAdded -= OnNodeAdded;

			bool ok = _rootScenes.Contains("StickerSelect");
			bool passed = ok && _passedBefore;
			GD.Print($"[SELFTEST] click \"返回\" -> scenes loaded: [{string.Join(", ", _rootScenes)}] (expect StickerSelect) : {ok}");
			GD.Print(passed ? "[SELFTEST] PASSED" : "[SELFTEST] FAILED");

			await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
			GetTree().Quit();
		}
	}

	private bool StripButtonLit(int index)
	{
		if (index < 0 || index >= _stripButtons.Count)
		{
			GD.PushError($"[SELFTEST] strip index {index} out of range");
			return false;
		}
		return _stripButtons[index].Modulate == HighlightTint;
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}
}