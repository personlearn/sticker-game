#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 冬日假期纸偶换装（贴纸游戏选择页里的「冬日假期换装」）。
///
/// 素材是 <c>assset/Winter Holiday Paper Doll/</c> 那套「打印用纸偶」，原始 PNG 是米色纸卡上
/// 带白色虚线剪裁框的整张贴纸（还混进了邻贴纸的碎块），所以先用工具离线抠图并把两个娃娃
/// 归一化到同一块 260x520 画布，成品放在同目录的 <c>cut/</c> 子目录里。
///
/// 这个玩法和其它换装最大的不同：**同时给两个娃娃换装**。
/// 男孩 / 女孩共用一整套衣服参数（因为归一化过），但各自的穿搭是独立的，
/// 底部面板最上面多一行「换装对象」用来切换现在给谁换，没在换的那个会压暗一点。
///
/// 玩法沿用 <see cref="StickerGame"/> 的手感：
/// <list type="bullet">
/// <item>上衣 / 下装 / 帽子 / 背景 —— 单选；</item>
/// <item>配饰 —— 多选开关，并且可以拖动摆放：按住拖 = 搬动贴纸，长按 = 切换「在角色身前 / 身后」；</item>
/// <item>身上的衣服 / 帽子 —— 穿上后也能按住拖动微调位置，换装或点「重置」时位置复位。</item>
/// </list>
///
/// 渲染分层（绝对 z_index，数值大的画在上面）：背景(-10) &lt; 配饰·身后(-5) &lt; 娃娃(0)
/// &lt; 下装(10) &lt; 上衣(11) &lt; 帽子(12) &lt; 配饰·身前(20) &lt; 拖动中(30)。
/// </summary>
public partial class WinterDressUp : Control
{
	// ---------- 分层 ZIndex 常量（绝对层号）----------
	private const int ZBottom = 10;     // 下装（牛仔裤 / 格子裙）
	private const int ZTop = 11;        // 上衣（夹克 / 开衫 / 毛衣），要压在下装上面
	private const int ZHat = 12;        // 帽子（毛线帽，盖在头上）
	private const int ZPropBack = -5;   // 配饰·娃娃身后（夹在背景 -10 与娃娃 0 之间）
	private const int ZPropFront = 20;  // 配饰·娃娃身前
	private const int ZDragging = 30;   // 拖动中的贴纸临时提到最前，松手还原

	// ---------- 初始造型（开局和「重置」都回到这里）----------
	// 男孩：蓝色夹克 + 牛仔裤 + 蓝毛线帽；女孩：绿开衫（自带围巾）+ 格子裙 + 红毛线帽。
	private const int InitialBoyTop = 1, InitialBoyBottom = 1, InitialBoyHat = 1;
	private const int InitialGirlTop = 2, InitialGirlBottom = 2, InitialGirlHat = 2;
	private const int InitialBg = 0;    // 雪夜

	private const string AssetDir = "res://assset/Winter Holiday Paper Doll/cut/";
	private const string DingPath = "res://sfx/ding.wav";

	// 两个娃娃已经被离线归一化到同一块 260x520 的画布（身体中轴 x=130、脚底 y=508），
	// 所以两个娃娃共用同一套服饰 Offset / Scale，区别只在舞台上的位置。
	private const float DollScale = 1f;
	private const float DollX = 165f;   // 两个娃娃相对舞台中心的水平距离
	private const float DollY = -24f;   // 竖直微调：让脚底落在舞台上不被底部面板压住
	private const float PanelHeight = 372f;

	// Toast 提示的顶边（画布坐标）。顶栏的下沿在 y=112，娃娃头顶在 y≈245，提示就摆在这条空档里。
	private const float ToastTop = 118f;

	// ---------- 拖拽手感参数 ----------
	private const float DragThreshold = 12f;      // 位移超过它才判定为拖拽（否则可能是点击/长按）
	private const double LongPressSeconds = 0.45; // 长按多久算长按
	private static readonly Color HighlightTint = new Color(1f, 0.92f, 0.45f);
	private static readonly Color InactiveDollTint = new Color(0.66f, 0.66f, 0.74f);

	// ---------- 场景节点 ----------
	private TextureRect _background = null!;
	private Node2D _character = null!;
	private readonly Sprite2D[] _dollSprite = new Sprite2D[2];
	private readonly Sprite2D[] _bottomSprite = new Sprite2D[2];
	private readonly Sprite2D[] _topSprite = new Sprite2D[2];
	private readonly Sprite2D[] _hatSprite = new Sprite2D[2];
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
	/// <summary>一件穿戴。<c>Tex == null</c> 表示「原装」/「不戴」（这一格不穿东西）。</summary>
	private sealed class WearItem
	{
		public string Label = "";
		public string? Tex;
		public Vector2 Offset;   // 相对娃娃中心的偏移（归一化画布坐标）
		public float Scale = 1f;
	}

	private sealed class PropItem
	{
		public string Label = "";
		public string Tex = "";
		public Vector2 StagePos;     // 舞台绝对坐标（拖动会改这里）
		public Vector2 HomePos;      // 初始舞台坐标，重置时归位
		public float Scale = 1f;
		public int Depth = ZPropFront;      // 绝对 z_index：在娃娃身前还是身后
		public int HomeDepth = ZPropFront;  // 初始层号，重置时还原
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

	// 衣服的 Offset 是「贴上时衣服的图中心相对娃娃画布中心的偏移」，Scale 是相对抠图后 PNG 的缩放。
	// 数值由离线合成校准工具（tools/_w_calib.py）目视定稿：所有上衣下摆对齐胯部、牛仔裤/裙子对齐腰线。
	private readonly List<WearItem> _tops = new()
	{
		new WearItem { Label = "原装", Tex = null },
		new WearItem { Label = "蓝色夹克", Tex = AssetDir + "blue_winter_jacket.png", Offset = new Vector2(0, 15), Scale = 0.926f },
		new WearItem { Label = "绿开衫", Tex = AssetDir + "green_cardigan_with_scarf.png", Offset = new Vector2(1, 15), Scale = 0.978f },
		new WearItem { Label = "绿风衣", Tex = AssetDir + "green_hooded_parka.png", Offset = new Vector2(0, 15), Scale = 0.907f },
		new WearItem { Label = "雪花毛衣", Tex = AssetDir + "fair_isle_sweater_multi.png", Offset = new Vector2(1, 15), Scale = 1.036f },
		new WearItem { Label = "红雪花毛衣", Tex = AssetDir + "fair_isle_sweater_red.png", Offset = new Vector2(2, 15), Scale = 0.962f },
	};

	private readonly List<WearItem> _bottoms = new()
	{
		new WearItem { Label = "原装", Tex = null },
		new WearItem { Label = "牛仔裤", Tex = AssetDir + "blue_jeans.png", Offset = new Vector2(-4, 126), Scale = 0.70f },
		new WearItem { Label = "格子裙", Tex = AssetDir + "red_plaid_skirt.png", Offset = new Vector2(0, 106), Scale = 0.60f },
	};

	private readonly List<WearItem> _hats = new()
	{
		new WearItem { Label = "不戴", Tex = null },
		new WearItem { Label = "蓝毛线帽", Tex = AssetDir + "blue_beanie.png", Offset = new Vector2(0, -175), Scale = 0.78f },
		new WearItem { Label = "红毛线帽", Tex = AssetDir + "red_beanie.png", Offset = new Vector2(5, -174), Scale = 0.79f },
	};

	// Depth：圣诞树 / 壁炉 / 扶手椅这类靠墙摆的应该在娃娃身后；拿在手里、摆在桌面上的在身前。
	private readonly List<PropItem> _propItems = new()
	{
		new() { Label = "圣诞树", Tex = "christmas_tree.png", StagePos = new Vector2(132, 545), Scale = 0.90f, Depth = ZPropBack },
		new() { Label = "壁炉", Tex = "fireplace.png", StagePos = new Vector2(592, 600), Scale = 0.65f, Depth = ZPropBack },
		new() { Label = "扶手椅", Tex = "green_armchair.png", StagePos = new Vector2(150, 792), Scale = 0.50f, Depth = ZPropBack },
		new() { Label = "红围巾", Tex = "red_knit_scarf.png", StagePos = new Vector2(330, 470), Scale = 0.62f },
		new() { Label = "绿手套", Tex = "green_mittens_pair.png", StagePos = new Vector2(398, 640), Scale = 0.62f },
		new() { Label = "热可可", Tex = "red_mug_hot_cocoa.png", StagePos = new Vector2(330, 758), Scale = 0.52f },
		new() { Label = "红礼盒", Tex = "gift_box_red.png", StagePos = new Vector2(215, 800), Scale = 0.50f },
		new() { Label = "绿礼盒", Tex = "gift_box_green.png", StagePos = new Vector2(268, 812), Scale = 0.50f },
		new() { Label = "白礼盒", Tex = "gift_box_white.png", StagePos = new Vector2(432, 806), Scale = 0.50f },
		new() { Label = "姜饼星", Tex = "gingerbread_star_cookie.png", StagePos = new Vector2(60, 690), Scale = 0.55f },
		new() { Label = "蓝雪花", Tex = "snowflake_blue_left.png", StagePos = new Vector2(330, 210), Scale = 0.55f },
		new() { Label = "金雪花", Tex = "snowflake_gold_left.png", StagePos = new Vector2(420, 150), Scale = 0.55f },
		new() { Label = "金铃铛", Tex = "token_gold_bell.png", StagePos = new Vector2(52, 300), Scale = 0.50f },
		new() { Label = "拐杖糖", Tex = "token_candy_cane.png", StagePos = new Vector2(668, 300), Scale = 0.50f },
		new() { Label = "雪房子", Tex = "token_winter_house.png", StagePos = new Vector2(660, 420), Scale = 0.50f },
		new() { Label = "松果", Tex = "token_pinecone.png", StagePos = new Vector2(330, 340), Scale = 0.50f },
	};

	private readonly List<BgItem> _bgs = new()
	{
		new BgItem { Label = "雪夜", Top = new Color("#2b3a67"), Bottom = new Color("#dfe9ff"), Decor = "bubbles" },
		new BgItem { Label = "暖屋", Top = new Color("#8a4a30"), Bottom = new Color("#ffe6c7"), Decor = "sun_low" },
		new BgItem { Label = "晴雪", Top = new Color("#9fc7e8"), Bottom = new Color("#f7fbff"), Decor = "sun" },
		new BgItem { Label = "薄荷", Top = new Color("#6fd0b8"), Bottom = new Color("#eafff7"), Decor = "bubbles" },
	};

	// ---------- 运行时状态 ----------
	private readonly List<ImageTexture> _bgTextures = new();
	private readonly Dictionary<Button, int> _categoryIndex = new();
	private readonly Dictionary<Button, int> _dollIndex = new();

	// 物品栏当前挂着的按钮，与 _stripIndices 一一对应。
	// 刷新高亮只改这两个列表里的按钮，不再重建节点（重建会毁掉点击弹跳动画）。
	private readonly List<BaseButton> _stripButtons = new();
	private readonly List<int> _stripIndices = new();

	private int _activeDoll;     // 0 男孩 / 1 女孩
	private int _activeCategory; // 0上衣 1下装 2帽子 3配饰 4背景
	// 两个娃娃各自的穿搭，互不影响
	private readonly int[] _topIndex = { InitialBoyTop, InitialGirlTop };
	private readonly int[] _bottomIndex = { InitialBoyBottom, InitialGirlBottom };
	private readonly int[] _hatIndex = { InitialBoyHat, InitialGirlHat };
	private int _bgIndex = InitialBg;

	private Vector2 _center;
	private Tween? _toastTween;
	private int _photoCounter;

	// ---------- 拖拽状态 ----------
	private PropItem? _pressProp;    // 当前被按住的配饰贴纸
	private Sprite2D? _pressWear;    // 当前被按住的穿戴物（衣服 / 裤子 / 帽子），与 _pressProp 二选一
	private Vector2 _pressPos;       // 按下时的画布坐标
	private Vector2 _grabOffset;     // 按下点相对贴纸中心的偏移，拖动时保持它不变，贴纸才不会跳
	private bool _dragging;          // 位移超过阈值，已判定为拖拽
	private bool _longPressFired;    // 长按已生效，松手时不用再收尾
	private int _pressToken;         // 每次按下自增，用来作废上一轮还在等待的长按计时
	private int _wearZBeforeDrag;    // 穿戴物拖动前的层号，松手还原
	private float _wearScaleBeforeDrag = 1f; // 穿戴物拖动前的缩放，松手还原
	private Rect2 _stageRect;        // 舞台可点区域（底部面板以上）

	public override void _Ready()
	{
		// 记住配饰的初始位置和初始层号，供「重置」还原
		foreach (var p in _propItems)
		{
			p.HomePos = p.StagePos;
			p.HomeDepth = p.Depth;
		}

		// 获取场景节点
		_background = GetNode<TextureRect>("Stage/Background");
		_character = GetNode<Node2D>("Stage/Character");
		string[] dolls = { "Boy", "Girl" };
		for (int d = 0; d < 2; d++)
		{
			_dollSprite[d] = GetNode<Sprite2D>($"Stage/Character/{dolls[d]}Doll");
			_bottomSprite[d] = GetNode<Sprite2D>($"Stage/Character/{dolls[d]}Bottom");
			_topSprite[d] = GetNode<Sprite2D>($"Stage/Character/{dolls[d]}Top");
			_hatSprite[d] = GetNode<Sprite2D>($"Stage/Character/{dolls[d]}Hat");
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

		SetProcessInput(true); // 拖拽靠 _Input 实现，确保输入回调是打开的

		// 音效文件缺失时静默跳过（项目里 sfx/ding.wav 目前并不存在）
		if (ResourceLoader.Exists(DingPath))
			_sfx.Stream = GD.Load<AudioStream>(DingPath);

		// 背景纹理预生成
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

		GD.Print($"[Winter] ready. selftest = {SelftestFlag.Describe()}");

		// 自测开关由首页路由过来：只有 selftest.flag 的内容正好是 "winter" 时才跑自测。
		if (SelftestFlag.Read() == SelftestFlag.TokenWinter)
			_ = RunSelfTestAsync();
		else
			_ = ShowHintAsync();
	}

	// ================= 布局 =================

	private void LayoutStage()
	{
		Vector2 size = Size.X > 0 && Size.Y > 0 ? Size : new Vector2(720, 1280);

		// 关键：让 Stage 与 UI 容器铺满全屏。
		// UI 容器尺寸为 0 时，其内 TopBar/BottomPanel 的宽度会塌缩为 0，导致顶部按钮与底部标签全部不可见。
		GetNode<Control>("Stage").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		GetNode<Control>("UI").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		float stageH = Mathf.Max(size.Y - PanelHeight, 400);
		_center = new Vector2(size.X * 0.5f, stageH * 0.54f);
		_character.Position = _center;

		// 舞台可点区域：面板以上。用于判断一次按下是「搬贴纸」还是「按面板」。
		_stageRect = new Rect2(0, 0, size.X, Mathf.Max(size.Y - PanelHeight, 200));

		for (int d = 0; d < 2; d++)
		{
			_dollSprite[d].Texture = GD.Load<Texture2D>(AssetDir + (d == 0 ? "boy_doll.png" : "girl_doll.png"));
			_dollSprite[d].Scale = Vector2.One * DollScale;
			_dollSprite[d].Position = DollRel(d);
		}

		// 刷新穿搭与配饰位置
		for (int d = 0; d < 2; d++)
		{
			ApplyTopFor(d, _topIndex[d], silent: true);
			ApplyBottomFor(d, _bottomIndex[d], silent: true);
			ApplyHatFor(d, _hatIndex[d], silent: true);
		}
		foreach (var p in _propItems)
		{
			if (p.Sprite == null || p == _pressProp)
				continue; // 正在拖的那张贴纸别被窗口缩放打断
			p.Sprite.Position = p.StagePos - _center;
		}
	}

	/// <summary>第 d 个娃娃相对舞台中心的偏移（0 男孩在左、1 女孩在右）。</summary>
	private static Vector2 DollRel(int d) => new Vector2(d == 0 ? -DollX : DollX, DollY);

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

		// 注意：这三个按钮在场景文件里的顺序是 返回 → 重置 → 拍照，
		// 下面的 spacer 必须插到「重置」之后（MoveChild 的索引是 2），插错了会把按钮挤到右边。
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

		// ---- 换装对象（这个玩法特有的：两个娃娃都要换）----
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
		string[] whoNames = { "男孩", "女孩" };
		for (int i = 0; i < whoNames.Length; i++)
		{
			var b = new Button
			{
				Text = whoNames[i],
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

		// ---- 分类标签（5 个：上衣 / 下装 / 帽子 / 配饰 / 背景）----
		_categoryTabs.AddThemeConstantOverride("separation", 10);
		string[] cats = { "上衣", "下装", "帽子", "配饰", "背景" };
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

		// ---- 物品栏（横向滚动，默认自动隐藏滚动条，支持触摸拖动） ----
		_itemScroll.CustomMinimumSize = new Vector2(0, 146);
		_itemStrip.AddThemeConstantOverride("separation", 16);

		// ---- Toast 提示 ----
		// 位置：横跨在角色头顶上方的空档里（顶栏下沿 y=112、娃娃头顶 y≈245）。
		// 两处必须显式设置的坑：
		//  ① Control 的 grow_direction 默认是 End（只向右/下扩）。锚在中心时会让整条提示
		//     从锚点往右溢出、被屏幕裁掉，所以水平必须是 Both（以锚点为中心向两侧扩）。
		//  ② 垂直用 End（= 向下扩，顶边钉住不动），这样提示再长也不会顶到上面的顶栏。
		_toast.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
		_toast.GrowHorizontal = GrowDirection.Both;
		_toast.GrowVertical = GrowDirection.End;
		_toast.OffsetTop = ToastTop;
		_toast.OffsetBottom = ToastTop;
		_toast.HorizontalAlignment = HorizontalAlignment.Center;
		_toast.MouseFilter = MouseFilterEnum.Ignore;
		// 提示画在舞台里，而穿搭的 z_index 是 11：z_index 比场景树顺序优先，
		// 不给 Toast 一个更高的层号，衣服就会盖在提示文字上。
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

	/// <summary>把没在换的那个娃娃整体压暗，一眼看出现在在给谁换装。</summary>
	private void RefreshDollFocus()
	{
		for (int d = 0; d < 2; d++)
		{
			Color m = d == _activeDoll ? Colors.White : InactiveDollTint;
			_dollSprite[d].Modulate = m;
			_bottomSprite[d].Modulate = m;
			_topSprite[d].Modulate = m;
			_hatSprite[d].Modulate = m;
		}
	}

	// ================= 物品栏 =================

	private void SelectDoll(int index, bool silent = false)
	{
		_activeDoll = Mathf.Clamp(index, 0, 1);
		RefreshDollTabs();
		RefreshDollFocus();
		RefreshStripHighlight();
		if (!silent)
		{
			PlayDing();
			ShowToast(_activeDoll == 0 ? "现在给男孩换装" : "现在给女孩换装");
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
			// 先 RemoveChild 再 QueueFree：QueueFree 要到帧末才真正删除，
			// 只调它的话这一帧容器里会同时挂着新旧两套按钮（布局会闪一下）。
			_itemStrip.RemoveChild(child);
			child.QueueFree();
		}
		_stripButtons.Clear();
		_stripIndices.Clear();

		switch (_activeCategory)
		{
			case 0: // 上衣
				BuildOptionStrip(_tops.Count, i => (_tops[i].Label, _tops[i].Tex), i => ApplyTop(i));
				break;
			case 1: // 下装
				BuildOptionStrip(_bottoms.Count, i => (_bottoms[i].Label, _bottoms[i].Tex), i => ApplyBottom(i));
				break;
			case 2: // 帽子
				BuildOptionStrip(_hats.Count, i => (_hats[i].Label, _hats[i].Tex), i => ApplyHat(i));
				break;
			case 3: // 配饰（多选开关）
				BuildPropStrip();
				break;
			case 4: // 背景
				BuildBgStrip();
				break;
		}

		RefreshStripHighlight();
	}

	private void AddStripButton(BaseButton b, int itemIndex)
	{
		_itemStrip.AddChild(b);
		_stripButtons.Add(b);
		_stripIndices.Add(itemIndex);
	}

	/// <summary>通用的「单选」物品栏：每格给一个 (名字, 贴图路径)，贴图为 null 即「原装 / 不戴」。</summary>
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

			// 闭包捕获的是当次循环的局部变量，不会被后续循环改掉（C# 循环变量陷阱的规避手法）
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
			// 生成小缩略图作为图标
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

	/// <summary>
	/// 就地刷新物品栏高亮。
	/// 以前是「换一次高亮就整条重建」，这会把刚按下的那个按钮 QueueFree 掉，
	/// 于是 PlayPop 的缩放动画一帧都播不完；重建还会让新旧按钮在同一帧里共存。
	/// 现在只改按钮的 Modulate，节点始终复用。
	/// </summary>
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
				2 => idx == _hatIndex[d],
				3 => idx >= 0 && idx < _propItems.Count && _propItems[idx].On,
				4 => idx == _bgIndex,
				_ => false,
			};
			_stripButtons[k].Modulate = active ? HighlightTint : Colors.White;
		}
	}

	// ================= 换装逻辑 =================

	/// <summary>给自己当前在换的那个娃娃穿上第 index 件上衣。</summary>
	private void ApplyTop(int index, bool silent = false) => ApplyTopFor(_activeDoll, index, silent);
	private void ApplyBottom(int index, bool silent = false) => ApplyBottomFor(_activeDoll, index, silent);
	private void ApplyHat(int index, bool silent = false) => ApplyHatFor(_activeDoll, index, silent);

	private void ApplyTopFor(int doll, int index, bool silent = false)
	{
		_topIndex[doll] = index;
		ApplyWearAt(_topSprite[doll], _tops, index, doll);
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplyBottomFor(int doll, int index, bool silent = false)
	{
		_bottomIndex[doll] = index;
		ApplyWearAt(_bottomSprite[doll], _bottoms, index, doll);
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplyHatFor(int doll, int index, bool silent = false)
	{
		_hatIndex[doll] = index;
		ApplyWearAt(_hatSprite[doll], _hats, index, doll);
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplyWearAt(Sprite2D target, List<WearItem> list, int index, int doll)
	{
		var it = list[index];
		if (it.Tex is null)
		{
			target.Texture = null;
		}
		else
		{
			target.Texture = GD.Load<Texture2D>(it.Tex);
			target.Scale = Vector2.One * it.Scale * DollScale;
			PlaceWear(target, DollRel(doll) + it.Offset * DollScale);
		}
	}

	/// <summary>把一件穿戴物摆到基准位置，并记下这个基准，供「换装 / 重置」复位。</summary>
	private static void PlaceWear(Sprite2D s, Vector2 basePos)
	{
		s.Position = basePos;
		s.SetMeta("wear_home", basePos);
	}

	/// <summary>两个娃娃所有可拖动的穿戴物（帽子 / 上衣 / 下装），按层号从高到低，命中时上面那件优先。</summary>
	private IEnumerable<Sprite2D> WearSprites()
	{
		for (int d = 0; d < 2; d++)
		{
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
				// Props 容器自己的 z_index = 20，而 z_index 默认是「相对父节点」的。
				// 关掉相对模式，Depth 才能直接当绝对层号用，和场景里的 0 / 10 / 11 比较。
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

	/// <summary>回到贴纸游戏选择页。</summary>
	private void OnHomePressed()
	{
		PlayPop(_homeButton);
		GetTree().ChangeSceneToFile(ScenePaths.StickerSelect);
	}

	/// <summary>
	/// 回到初始造型：两个娃娃各自穿好，配饰全收起并归位，背景回到雪夜。
	/// 开局和「重置」按钮都走这里，保证「重置」真的等于「回到开局」。
	/// </summary>
	private void ApplyInitialLook(bool silent = true)
	{
		_topIndex[0] = InitialBoyTop; _topIndex[1] = InitialGirlTop;
		_bottomIndex[0] = InitialBoyBottom; _bottomIndex[1] = InitialGirlBottom;
		_hatIndex[0] = InitialBoyHat; _hatIndex[1] = InitialGirlHat;
		for (int d = 0; d < 2; d++)
		{
			ApplyTopFor(d, _topIndex[d], silent: true);
			ApplyBottomFor(d, _bottomIndex[d], silent: true);
			ApplyHatFor(d, _hatIndex[d], silent: true);
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
	//
	// 一套手势解决鼠标和触摸：按下 → 位移超过阈值就是「拖拽」，按住不动 0.45 秒就是「长按」。
	// 项目里同时开了 emulate_touch_from_mouse / emulate_mouse_from_touch，
	// 一次点击会同时产生鼠标事件和触摸事件，所以两个分支都要处理，并用 _pressProp 去重。

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
			// 触摸屏（Android）：按下 / 拖动 / 抬起
			case InputEventScreenTouch st:
				consumed = st.Pressed ? BeginPress(st.Position) : EndPress();
				break;
			case InputEventScreenDrag sd:
				consumed = MovePress(sd.Position);
				break;
		}

		// 正在搬贴纸时把事件吃掉，否则手指划过底部面板会让它跟着一起滚
		if (consumed)
			GetViewport().SetInputAsHandled();
	}

	/// <summary>按下：看看有没有点到贴纸或身上穿的衣服。返回 true 表示这次按下归我们管。</summary>
	private bool BeginPress(Vector2 canvasPos)
	{
		if (_pressProp != null || _pressWear != null)
			return false; // 已经按住一张了（多半是鼠标/触摸重复事件）

		// 点在底部面板或顶栏上就别抢，交给 UI
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

	/// <summary>移动：超过阈值就升级成拖拽，把贴纸 / 衣服搬到指针位置。</summary>
	private bool MovePress(Vector2 canvasPos)
	{
		var prop = _pressProp;
		var wear = _pressWear;
		if (prop?.Sprite == null && wear == null)
			return false; // 这一指没在按东西
		if (_longPressFired)
			return true;  // 长按已经把这张贴纸处理掉了，这次按住不再拖动

		if (!_dragging)
		{
			if (canvasPos.DistanceTo(_pressPos) < DragThreshold)
				return true;
			_dragging = true;
			_pressToken++; // 作废长按计时
			if (prop?.Sprite != null)
			{
				prop.Sprite.ZIndex = ZDragging;                       // 拖动中先提到最前，免得躲进角色身后
				prop.Sprite.Scale = Vector2.One * prop.Scale * 1.06f; // 放大一点点，给"拿起来了"的反馈
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

	/// <summary>松手：配饰把落点写回数据；衣服位置就地留在节点上，只还原层级和缩放。</summary>
	private bool EndPress()
	{
		var prop = _pressProp;
		var wear = _pressWear;
		if (prop == null && wear == null)
			return false;

		_pressProp = null;
		_pressWear = null;
		_pressToken++; // 作废还在等待的长按计时

		if (_dragging)
		{
			if (prop?.Sprite != null)
			{
				prop.StagePos = prop.Sprite.GlobalPosition; // 写回数据，窗口缩放/重置时才有正确基准
				prop.Sprite.Scale = Vector2.One * prop.Scale;
				prop.Sprite.ZIndex = prop.Depth;
			}
			else if (wear != null)
			{
				// 衣服的位置直接留在节点上；下次换装 / 重置时 ApplyWearAt 会按基准重新摆好
				wear.Scale = Vector2.One * _wearScaleBeforeDrag;
				wear.ZIndex = _wearZBeforeDrag;
			}
		}

		bool wasDragging = _dragging;
		_dragging = false;
		_longPressFired = false;
		return wasDragging;
	}

	/// <summary>长按：切换这张贴纸在角色身前 / 身后。</summary>
	private async void ArmLongPress(int token, PropItem p)
	{
		await ToSignal(GetTree().CreateTimer(LongPressSeconds), SceneTreeTimer.SignalName.Timeout);
		if (token != _pressToken || _pressProp != p || _dragging)
			return; // 已经松手，或者中途改判成拖拽了
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

	/// <summary>找出这个坐标点下最上层的贴纸：层号大的优先，同层则后加入的优先。</summary>
	private PropItem? PickProp(Vector2 canvasPos)
	{
		PropItem? best = null;
		int bestZ = int.MinValue;
		foreach (var p in _propItems)
		{
			if (!p.On || p.Sprite == null)
				continue;
			// Sprite2D.GetRect() 是「以中心为原点」的本地矩形，正好能当点击区域用
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

	/// <summary>
	/// 拍照。取的是「上一帧画完的结果」，所以要先藏掉 UI、等两帧再抓，
	/// 否则照片里会带上顶栏按钮和上一句提示。
	/// </summary>
	private async void OnPhotoPressed()
	{
		PlayPop(_photoButton);
		PlayDing();

		var vp = GetViewport();
		bool topBarWasVisible = _topBar.Visible;
		bool toastWasVisible = _toast.Visible;
		_topBar.Visible = false;
		_toast.Visible = false;
		// 照片里两个娃娃都是正常亮度（压暗只是「正在给谁换」的提示）
		for (int d = 0; d < 2; d++)
		{
			_dollSprite[d].Modulate = Colors.White;
			_bottomSprite[d].Modulate = Colors.White;
			_topSprite[d].Modulate = Colors.White;
			_hatSprite[d].Modulate = Colors.White;
		}

		// 等两帧：第一帧画完「没有 UI」的画面，第二帧才保证拿得到它
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		var img = vp.GetTexture().GetImage();

		_topBar.Visible = topBarWasVisible;
		_toast.Visible = toastWasVisible;
		RefreshDollFocus();

		// 舞台区域：整个舞台，到底部面板上沿为止
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
		string path = $"user://screenshots/winter_{stamp}_{_photoCounter}.png";
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

	/// <summary>开局教一次玩法（自测模式下不弹）。写成一行：Toast 钉在娃娃头顶那条空档里，两行会压到头发。</summary>
	private async Task ShowHintAsync()
	{
		await Wait(1.0);
		ShowToast("先选男孩 / 女孩 · 按住拖动贴纸 · 长按翻层");
	}

	// ================= 程序化背景生成 =================
	//
	// 这里最容易踩的坑：Image.FillRect 是「直接写像素」，不做 alpha 混合。
	// 想画一层半透明的东西，必须自己先算好混合结果、再写不透明的颜色；
	// 只要写进去的 alpha != 1，那一块就真的变成半透明像素，游戏清屏色会透出来。
	// 小工具：FillRow 负责「在一行里写一段不透明像素」，并强制 alpha = 1。

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

	/// <summary>第 y 行的天空底色。渐变只跟 y 有关，所以每行是个纯色，画装饰时可以直接拿它当混合底色。</summary>
	private static Color SkyAt(BgItem bg, int y, int h)
	{
		float t = h > 1 ? y / (float)(h - 1) : 0f;
		return bg.Top.Lerp(bg.Bottom, t);
	}

	/// <summary>在一行里写一段像素，越界自动裁剪，并强制写成不透明。</summary>
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

	/// <summary>柔光圆斑（太阳 / 泡泡）。半透明是「手算」出来的：拿天空底色往光晕色插值，再写不透明像素。</summary>
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
			// 自测是 fire-and-forget 协程，异常会被 Task 静默吞掉（表现为日志戛然而止、进程不退出）
			GD.Print($"[SELFTEST] CRASHED: {e}");
			GetTree().Quit();
		}
	}

	private async Task RunSelfTestBodyAsync()
	{
		GD.Print("[SELFTEST] begin (winter)");
		await Wait(0.4);

		int fails = 0;

		// ① 纹理加载：两个娃娃 + 三类穿戴 + 配饰
		for (int d = 0; d < 2; d++)
			if (_dollSprite[d].Texture == null) { GD.PushError($"[SELFTEST] doll#{d} texture null"); fails++; }
		foreach (var o in _tops)
			if (o.Tex != null && GD.Load<Texture2D>(o.Tex) == null) { GD.PushError($"[SELFTEST] top {o.Label} null"); fails++; }
		foreach (var o in _bottoms)
			if (o.Tex != null && GD.Load<Texture2D>(o.Tex) == null) { GD.PushError($"[SELFTEST] bottom {o.Label} null"); fails++; }
		foreach (var o in _hats)
			if (o.Tex != null && GD.Load<Texture2D>(o.Tex) == null) { GD.PushError($"[SELFTEST] hat {o.Label} null"); fails++; }
		foreach (var p in _propItems)
			if (p.Sprite?.Texture == null) { GD.PushError($"[SELFTEST] prop {p.Label} texture null"); fails++; }
		GD.Print("[SELFTEST] textures: ok");

		// ② 背景完全不透明（FillRect 不做 alpha 混合，写错 alpha 会在背景上留下真正的透明窟窿）
		int translucent = 0;
		foreach (var tex in _bgTextures)
		{
			byte[] data = tex.GetImage().GetData(); // Format.Rgba8 → 每 4 字节一个像素
			for (int i = 3; i < data.Length; i += 4)
				if (data[i] != 255)
					translucent++;
		}
		if (translucent > 0)
		{
			GD.PushError($"[SELFTEST] background has {translucent} translucent pixels");
			fails++;
		}
		else
			GD.Print("[SELFTEST] background opaque: ok");

		// ③ 初始造型：两个娃娃各自的穿搭都应该是开局那套
		bool initialOk = _topIndex[0] == InitialBoyTop && _bottomIndex[0] == InitialBoyBottom && _hatIndex[0] == InitialBoyHat &&
						 _topIndex[1] == InitialGirlTop && _bottomIndex[1] == InitialGirlBottom && _hatIndex[1] == InitialGirlHat &&
						 _bgIndex == InitialBg;
		GD.Print($"[SELFTEST] initial look: boy(t{_topIndex[0]}/b{_bottomIndex[0]}/h{_hatIndex[0]}) " +
				 $"girl(t{_topIndex[1]}/b{_bottomIndex[1]}/h{_hatIndex[1]}) bg={_bgIndex} -> {initialOk}");
		if (!initialOk) fails++;

		// ④ 两个娃娃的穿搭互相独立：先记下男孩的上衣，给女孩换一件，男孩不应受影响
		int boyTopBefore = _topIndex[0];
		SelectDoll(1, silent: true);
		ApplyTop(3);
		await Wait(0.12);
		bool independent = _topIndex[1] == 3 && _topIndex[0] == boyTopBefore;
		GD.Print($"[SELFTEST] independent looks: girl top=3 boy top={_topIndex[0]} (expect {boyTopBefore}) -> {independent}");
		if (!independent) fails++;
		SelectDoll(0, silent: true);

		// 逐分类切换（每个分类都把每一档走一遍）
		for (int i = 0; i < _tops.Count; i++) { ApplyTop(i); await Wait(0.1); }
		ApplyTop(InitialBoyTop);
		for (int i = 0; i < _bottoms.Count; i++) { ApplyBottom(i); await Wait(0.1); }
		ApplyBottom(InitialBoyBottom);
		for (int i = 0; i < _hats.Count; i++) { ApplyHat(i); await Wait(0.1); }
		ApplyHat(InitialBoyHat);
		for (int i = 0; i < _bgs.Count; i++) { ApplyBackground(i); await Wait(0.1); }
		ApplyBackground(InitialBg);
		SelectDoll(1, silent: true);
		for (int i = 0; i < _tops.Count; i++) { ApplyTop(i); await Wait(0.1); }
		ApplyTop(InitialGirlTop);
		SelectDoll(0, silent: true);

		// 「原装」档位的高亮也要跟着走
		SelectCategory(0, silent: true);
		ApplyTop(0);
		await Wait(0.1);
		bool stripOffOk = StripButtonLit(0);
		GD.Print($"[SELFTEST] \"原装\" button highlighted: {stripOffOk}");
		if (!stripOffOk) fails++;
		ApplyTop(InitialBoyTop);
		await Wait(0.1);

		// 身上的衣服也能拖动：抓住男孩的上衣 → 往下挪 → 松手，位置就地生效；重新换同一件要复位。
		// 此刻配饰还没全开，舞台上能点到的穿戴物只有两个娃娃的衣服，不会互相抢。
		var boyTopSprite = _topSprite[0];
		Vector2 boyTopHome = boyTopSprite.GlobalPosition;
		bool wearGrab = BeginPress(boyTopSprite.GlobalPosition);
		MovePress(boyTopSprite.GlobalPosition + new Vector2(36, 44));
		bool wearDrop = EndPress();
		float wearMoved = boyTopSprite.GlobalPosition.DistanceTo(boyTopHome);
		bool wearDragOk = wearGrab && wearDrop && wearMoved > 20f;
		GD.Print($"[SELFTEST] drag boy jacket: grab={wearGrab} drop={wearDrop} moved={wearMoved:0.#}px -> {boyTopSprite.Position}");
		if (!wearDragOk) fails++;

		Vector2 boyTopBase = DollRel(0) + _tops[InitialBoyTop].Offset * DollScale;
		ApplyTop(InitialBoyTop);
		await Wait(0.1);
		bool wearResetOk = boyTopSprite.Position.IsEqualApprox(boyTopBase);
		GD.Print($"[SELFTEST] re-wear jacket resets position: {boyTopSprite.Position} vs {boyTopBase} -> {wearResetOk}");
		if (!wearResetOk) fails++;

		// 换装对象那一行：两颗按钮都在，且高亮跟着 _activeDoll 走
		bool dollTabsOk = _dollIndex.Count == 2;
		foreach (var kv in _dollIndex)
			if (kv.Key.Text != (kv.Value == 0 ? "男孩" : "女孩"))
				dollTabsOk = false;
		GD.Print($"[SELFTEST] doll tabs: {_dollIndex.Count} buttons -> {dollTabsOk}");
		if (!dollTabsOk) fails++;

		// 配饰全开
		foreach (var p in _propItems) { ToggleProp(p); await Wait(0.05); }
		await Wait(0.3);

		// 拖拽：抓住第一张贴纸 → 往下移动 → 松手，坐标要写回数据。
		// 落点选在舞台正中的空当里：Sprite2D.GetRect() 是「不含节点 scale 的原始图尺寸」，
		// 命中框比看到的大一圈，落在别的贴纸命中框里的话，下面长按会点到叠在它上面的那张。
		var probe = _propItems[0];
		Vector2 startPos = probe.StagePos;
		bool grabOk = BeginPress(startPos);
		MovePress(startPos + new Vector2(10, 325));
		bool dropOk = EndPress();
		float movedBy = probe.StagePos.DistanceTo(startPos);
		bool dragOk = grabOk && dropOk && movedBy > 50f;
		GD.Print($"[SELFTEST] drag {probe.Label}: grab={grabOk} drop={dropOk} moved={movedBy:0.#}px -> {probe.StagePos}");
		if (!dragOk) fails++;

		// 点空白处不应该抓到任何贴纸
		bool emptyMiss = BeginPress(new Vector2(Size.X - 4, 4)) == false;
		GD.Print($"[SELFTEST] tap on empty area grabs nothing: {emptyMiss}");
		if (!emptyMiss) fails++;

		// 拍照（趁配饰全开、画面最满的时候拍）
		OnPhotoPressed();
		await Wait(0.8);

		// 长按 0.45 秒：切换这张贴纸在角色身前 / 身后。
		// 只留探头这一张贴纸：Sprite2D.GetRect() 是「不含节点 scale 的原始图尺寸」，
		// 命中框比看到的大一圈，别的贴纸会把点击抢走（同层时后加入的优先）。
		foreach (var p in _propItems)
			if (p != probe && p.On)
				ToggleProp(p);
		await Wait(0.15);
		int depthBefore = probe.Depth;
		bool lpGrab = BeginPress(probe.StagePos);
		await Wait(0.7); // 超过长按判定时间
		bool depthFlipped = probe.Depth != depthBefore && probe.Sprite != null && probe.Sprite.ZIndex == probe.Depth;
		EndPress();
		await Wait(0.1);
		GD.Print($"[SELFTEST] long press flips depth: grab={lpGrab} {depthBefore} -> {probe.Depth} ({depthFlipped})");
		if (!depthFlipped) fails++;

		// 重置：两个娃娃都回到初始造型，配饰收起并归位
		ApplyInitialLook(silent: false);
		await Wait(0.4);
		bool resetOk = probe.StagePos.IsEqualApprox(probe.HomePos) && probe.Depth == probe.HomeDepth && !probe.On &&
					   _topIndex[0] == InitialBoyTop && _bottomIndex[0] == InitialBoyBottom && _hatIndex[0] == InitialBoyHat &&
					   _topIndex[1] == InitialGirlTop && _bottomIndex[1] == InitialGirlBottom && _hatIndex[1] == InitialGirlHat &&
					   _bgIndex == InitialBg;
		GD.Print($"[SELFTEST] after reset: boy(t{_topIndex[0]}/b{_bottomIndex[0]}/h{_hatIndex[0]}) " +
				 $"girl(t{_topIndex[1]}/b{_bottomIndex[1]}/h{_hatIndex[1]}) bg={_bgIndex} probeHome={resetOk}");
		if (!resetOk) fails++;

		// 验证截图文件
		string dir = ProjectSettings.GlobalizePath("user://screenshots");
		bool found = System.IO.Directory.Exists(dir) &&
					 System.IO.Directory.GetFiles(dir, "winter_*.png").Length > 0;
		GD.Print($"[SELFTEST] photo exists: {found}");
		if (!found) fails++;

		// 顶栏：三颗按钮都在、标签对、而且「返回」真的接上了回调
		bool topBarOk = _homeButton.Visible && _resetButton.Visible && _photoButton.Visible &&
						_homeButton.Text == "返回" && _resetButton.Text == "重置" && _photoButton.Text == "拍照" &&
						_homeButton.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0;
		GD.Print($"[SELFTEST] top bar: \"{_homeButton.Text}\" / \"{_resetButton.Text}\" / \"{_photoButton.Text}\" -> {topBarOk}");
		if (!topBarOk) fails++;

		// 整屏截一张，方便肉眼核对顶栏与底部面板布局
		SaveShot("winter");

		bool passed = fails == 0;
		GD.Print(passed ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} problem(s))");

		// 最后一项：点「返回」要真的回到贴纸游戏选择页。
		// 换场景会把本场景连同这里的协程一起销毁，所以：
		//   a) 校验交给挂在树根上、不随场景销毁的见证节点；
		//   b) 这行之后**不能再 await**（await 的续体会跟着本场景一起死掉）。
		var witness = new BackWitness(passed);
		GetTree().Root.AddChild(witness);
		_ = witness.VerifyAsync();
		_homeButton.EmitSignal(BaseButton.SignalName.Pressed);
	}

	/// <summary>整屏截图（自测用；游戏的「拍照」只截角色框，看不见顶栏）。</summary>
	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Winter] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	/// <summary>
	/// 「换场景之后」的见证者。本场景一换就被销毁，它自己的协程会跟着死，
	/// 所以把校验放到挂在树根上的这个节点里。
	///
	/// 用「监听 NodeAdded」而不是「逐帧看 CurrentScene」：换过来的场景内部还有一大堆子节点，
	/// 监听 NodeAdded 能直接拿到「挂到树根上的那个场景」，不必逐帧采样。
	/// </summary>
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

		/// <summary>只收「直接挂到树根上的场景」，场景内部那堆子节点不算。</summary>
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

			// 说明：现在「返回」去的是贴纸选择页；选择页见 flag 不是 "select"，不会再自动往下走，
			// 所以这里就是最后一轮，稍等一拍后直接退出进程。
			await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
			GetTree().Quit();
		}
	}

	/// <summary>自测辅助：物品栏第 index 个按钮是否处于高亮态。</summary>
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