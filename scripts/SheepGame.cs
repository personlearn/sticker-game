#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 羊了个羊（堆叠三消）—— 首页里的「堆叠消除」。
///
/// 玩法：一堆方块层层叠着，被上层压住的方块点不动；点一张就把这张送进底部卡槽，
/// 卡槽里凑齐 3 张同图案就一起消掉。卡槽塞满还没消掉就输，全部消完就赢。
///
/// 和前面两个游戏最不一样的地方是「<b>关卡是随机生成的，但一定可解</b>」。
/// 原版羊了个羊被人诟病的点，是它的堆叠方式可以用数学证明「怎么点都过不了」。
/// 这里的做法是反过来的：<b>先造一条合法的通关路线，再按这条路线去发牌</b>——
/// 见 <see cref="AssignTypes"/>，那条路线同时也被自测当作「可解的证明」复算一遍。
///
/// 结构上延续前两个游戏：瘦场景 + 一个脚本，方块都是「只有数据 + 自己画自己」的哑节点，
/// 规则和生成算法全部集中在这个文件里。所有图案都是 <c>_Draw</c> 现画的，不依赖任何素材。
/// </summary>
public partial class SheepGame : Control
{
	/// <summary>图案种类数。和原版一样 16 种。</summary>
	public const int TypeCount = 16;

	/// <summary>卡槽数量可选范围。</summary>
	public const int MinSlots = 6, MaxSlots = 10, DefaultSlots = 7;

	/// <summary>关卡数。</summary>
	public const int LevelCount = 5;

	/// <summary>关卡进度存档。</summary>
	private const string ProgressPath = "user://sheep_progress.cfg";

	// ===================== 关卡 =====================

	/// <summary>
	/// 「书页叠边」的宽度（单位 = 格子边长）：一张牌把下面那张**整张盖住**时，就在它右下方
	/// 画出一两道错开的边（<see cref="Tile.HiddenBelow"/> 数着下面压了几张，最多画三层），
	/// 看上去就是摞起来的书页——正好标在「两张完全重合」的地方，<b>不限层数</b>：
	/// 只要出现「两张及以上完全重叠」，那里就要有书页堆叠的效果。
	///
	/// 为什么不真的把上面那张挪一点：只要同一层的牌是紧挨着的，就<b>挪不动</b>——
	/// 被压那张露出的那条边，会被旁边那张同层牌的邻居正好补上（实测：整层挪 λ 后，
	/// 第 2~5 关仍各有 2~16 张整张被盖住，只有 2 层的第 1 关正常）。逐张挪也不行：
	/// 一张牌常常同时给好几张当盖子，几个方向会互相抵消。所以画出来最稳，
	/// 而且只影响这十几像素的装饰，牌的位置、点击判定全都不动。
	/// </summary>
	private const float StackLip = 0.045f;

	/// <summary>
	/// 一关的「形状」。
	/// 难度由四个旋钮一起决定：<b>图案种类数</b>（<see cref="Types"/>，最影响手感）、
	/// <b>方块总数</b>（由层表算出来）、<b>层数</b>（遮挡深度）、<b>交错程度</b>（<see cref="GenCap"/>）。
	/// </summary>
	private sealed class LevelDef
	{
		public string Name = "";

		/// <summary>
		/// 从下往上的每一层：<c>(列数, 行数)</c>。
		/// 约束（<see cref="BuildGeometry"/> 依赖它）：列/行单调不增，且每次最多减 1。
		/// 满足这个约束时，只要相邻两层错开半格，下面一层的每一张都一定被上面一层压住。
		/// </summary>
		public (int C, int R)[] Layers = System.Array.Empty<(int, int)>();

		/// <summary>
		/// 本关用**几种**图案（不是「总共有几种」——总共 16 种，但第 1 关只用前 6 种）。
		///
		/// 这是难度手感最主要的来源：图案越少，「能点的牌里凑得出三张同款」越容易。
		/// 一开始就上 16 种的话，最小的一关也会因为「翻遍全场找不到第三个」而变得很烦人——
		/// 那不是难度，是折磨。所以它从 6 种一路涨到 16 种。
		/// </summary>
		public int Types = TypeCount;

		/// <summary>
		/// 随机挖掉多少个方块。
		/// 不挖洞的话「只有最上面一层能点」，玩家几乎没有选择余地，也不好摆出好看的图案分布。
		/// 每个洞会让下面一层露出来，于是可点的方块就散布在好几层上。
		/// </summary>
		public int Holes;

		/// <summary>
		/// 生成答案时最多同时占用几个卡槽。
		/// 3 = 三张同款连着拿（最松，答案一眼能看懂）；5 = 五组交错着拿（越紧，留给玩家的余量越少）。
		/// </summary>
		public int GenCap;
	}

	/// <summary>
	/// 五关的难度阶梯。四个维度<b>同时</b>往上走，而且第一关刻意做得非常小：
	/// <code>
	/// 关  图案   方块   层   交错
	/// 1    6种    18    2    3
	/// 2    8种    30    3    3
	/// 3   10种    45    3    4
	/// 4   12种    60    4    4
	/// 5   16种    84    4    5
	/// </code>
	/// 张数都刻意凑成「3 的倍数」，而且「组数 ≥ 图案种类数」——这样每种图案至少能出现一组，
	/// 玩家在这一关里见得到全部图案，不会出现「有一种图案从头到尾没露面」的怪事。
	/// 层数不用封顶：方块大小一致，遮挡关系只由层表决定，叠多少层都不会出问题。
	/// </summary>
	private static readonly LevelDef[] Levels =
	{
		new LevelDef
		{
			Name = "初见", GenCap = 3, Holes = 0, Types = 6,
			Layers = new (int, int)[] { (4, 3), (3, 2) },                        // 12+6 = 18 张
		},
		new LevelDef
		{
			Name = "小坡", GenCap = 3, Holes = 1, Types = 8,
			Layers = new (int, int)[] { (4, 4), (3, 3), (3, 2) },                // 16+9+6-1 = 30 张
		},
		new LevelDef
		{
			Name = "叠塔", GenCap = 4, Holes = 3, Types = 10,
			Layers = new (int, int)[] { (5, 4), (4, 4), (4, 3) },                // 20+16+12-3 = 45 张
		},
		new LevelDef
		{
			Name = "高台", GenCap = 4, Holes = 2, Types = 12,
			Layers = new (int, int)[] { (5, 5), (4, 4), (4, 3), (3, 3) },        // 25+16+12+9-2 = 60 张
		},
		new LevelDef
		{
			Name = "羊王", GenCap = 5, Holes = 3, Types = 16,
			Layers = new (int, int)[] { (6, 5), (5, 5), (5, 4), (4, 3) },        // 30+25+20+12-3 = 84 张
		},
	};

	// ===================== 难度 =====================

	/// <summary>难易程度：只动「层数」和「交错程度」，不动卡槽数量。</summary>
	private enum Diff { Easy, Normal, Hard }

	private static readonly string[] DiffNames = { "轻松", "普通", "挑战" };

	private static readonly string[] DiffDesc =
	{
		"少一层 · 答案最松",
		"标准层数 · 标准交错",
		"多一层 · 交错更紧",
	};

	/// <summary>难度带来的层数增减（加在最上面）。</summary>
	private static int DiffLayerDelta(Diff d) => d switch
	{
		Diff.Easy => -1,
		Diff.Hard => 1,
		_ => 0,
	};

	/// <summary>难度带来的「交错程度」增减。</summary>
	private static int DiffCapDelta(Diff d) => d switch
	{
		Diff.Easy => -1,
		Diff.Hard => 1,
		_ => 0,
	};

	// ===================== 布局常量（720×1280 画布）=====================

	private const float BoardW = 680f;     // 棋盘可用区域（顶栏下沿 ~166 到卡槽上沿 ~870 之间）
	private const float BoardH = 648f;
	private const float BoardCx = 360f;    // 棋盘区域中心
	private const float BoardCy = 496f;
	private const float TileMin = 34f, TileMax = 100f;

	private const float SlotCy = 946f;     // 卡槽那一行的中心
	private const float SlotMaxW = 648f;
	private const float SlotTileMax = 66f, SlotTileMin = 42f;
	private const float SlotGap = 6f;

	private const int ZScenery = -10;
	private const int ZSlotFrames = 90, ZSlotTiles = 92, ZOverlay = 200;

	// ===================== 运行状态 =====================

	private enum Phase { Setup, Play, Win, Lose }

	private Phase _phase = Phase.Setup;
	private Diff _diff = Diff.Normal;
	private int _levelIndex;          // 0 ~ LevelCount-1
	private int _slots = DefaultSlots;
	private int _genCap = 3;
	private int _types = TypeCount;   // 本关用到几种图案
	private int _seed;
	private int _picks;               // 玩家点了几张（自测与结算用）
	private int _clearedTriples;      // 消掉了几组

	// 存档
	private readonly bool[] _cleared = new bool[LevelCount];
	private int _unlocked = 1;

	// 本局
	private readonly List<(int C, int R)> _layers = new();
	private readonly List<Tile> _tiles = new();
	private readonly List<Tile> _slots_ = new();   // 卡槽里的方块，按从左到右的顺序
	private readonly List<AudioStreamPlayer> _sfxPool = new();
	private int _inPile;                            // 还在棋盘上的方块数
	private int _sfxIndex;
	private float _tileSize = 88f;
	private float _slotTile = 60f;
	private List<int>[] _covers = System.Array.Empty<List<int>>();
	private List<int>[] _hiders = System.Array.Empty<List<int>>();   // 反过来的表：盖住我的那些牌
	private int[] _coverCount = System.Array.Empty<int>();
	private int[] _route = System.Array.Empty<int>();   // 本局的「标准答案」，自测用它证明这一局可解
	private int _hiddenTiles;   // 整张被盖住的牌有几张（统计用；埋得深的属于正常，不用管）
	private int _hiddenTop;     // 其中落在「上面两层」的有几张 —— 只有这些需要画书页叠边
	private int _badgedTiles;   // 身上画了书页叠边的牌有几张
	private int _coveredTiles;  // 身上压着牌的牌有几张
	private int _minSamples = 25;   // 全场露得最少的那张牌，25 个采样点里还剩几个可见

	// 节点
	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _board = null!;
	private Node2D _slotFrames = null!;
	private Node2D _slotTiles = null!;
	private SceneryView _scenery = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _setupButton = null!;
	private Button _restartButton = null!;
	private Label _info = null!;
	private Label _toast = null!;
	private Control _setup = null!;
	private Label _hintLabel = null!;
	private readonly Button[] _levelButtons = new Button[LevelCount];
	private readonly Label[] _levelNames = new Label[LevelCount];
	private readonly Label[] _levelMarks = new Label[LevelCount];
	private readonly Button[] _diffButtons = new Button[3];
	private readonly Button[] _slotButtons = new Button[MaxSlots - MinSlots + 1];
	private Button _startButton = null!;
	private ColorRect _dim = null!;
	private Label _overTitle = null!;
	private Label _overInfo = null!;
	private Label _overExtra = null!;
	private Label _overFooter = null!;
	private Button _nextButton = null!;
	private Button _retryButton = null!;
	private Button _toSetupButton = null!;
	private Tween? _toastTween;
	private ulong _lastPressMs;
	private Vector2 _lastPressPos = new Vector2(-9999f, -9999f);

	// ===================== 启动 =====================

	public override void _Ready()
	{
		_stage = GetNode<Control>("Stage");
		_background = GetNode<TextureRect>("Stage/Background");
		_board = GetNode<Node2D>("Stage/Board");
		_slotFrames = GetNode<Node2D>("Stage/SlotFrames");
		_slotTiles = GetNode<Node2D>("Stage/SlotTiles");
		_ui = GetNode<Control>("UI");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_setupButton = GetNode<Button>("UI/TopBar/SetupButton");
		_restartButton = GetNode<Button>("UI/TopBar/RestartButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		Theme = GameArt.MakeUiTheme();
		SetProcessInput(true);

		// 方块在棋盘上的 z 就是它的层号（0~7），所以整块 UI 必须提到它们之上，
		// 否则信息条、顶部按钮、结算面板都会被方块盖住。
		_ui.ZIndex = 1000;

		// 背景：草地渐变（GradientTexture2D 是 GPU 侧的，不占内存）
		_background.Texture = GameArt.VerticalGradient(new Color("#f8ecae"), new Color("#5d9c62"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		LoadProgress();
		BuildSfxPool();

		// 装饰层：云、太阳、草坡。纯装饰。
		_scenery = new SceneryView { ZIndex = ZScenery, ZAsRelative = false };
		_stage.AddChild(_scenery);

		// 卡槽的底框是空槽，单独画一层（放在卡槽方块下面）
		_slotFrames.AddChild(new SlotFramePainter { Host = this });

		BuildTopBar();
		BuildInfo();
		BuildToast();
		BuildSetup();
		BuildOverlay();
		Layout();

		GetViewport().SizeChanged += Layout;

		GD.Print($"[Sheep] ready. unlocked={_unlocked} level={_levelIndex + 1} " +
				 $"diff={DiffNames[(int)_diff]} slots={_slots} selftest={SelftestFlag.Describe()}");

		// 自测开关由首页路由过来：内容正好是 "sheep" 才跑本游戏的自测
		if (SelftestFlag.Read() == SelftestFlag.TokenSheep)
			_ = RunSelfTestAsync();
		else
			ShowSetup();
	}

	private void Layout()
	{
		// Control 不会自动铺满父节点，尺寸为 0 的话里面的东西全看不见
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」：
		// TextureRect 的 min size 默认跟着纹理走，而渐变纹理只有 8×256，
		// 不铺满就只在左上角画一小块，其余全是视口清屏色（0.3 灰）。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		if (_phase == Phase.Play)
			Relayout();
	}

	// ===================== 存档 =====================

	private void LoadProgress()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(ProgressPath) != Error.Ok)
			return;

		for (int i = 0; i < LevelCount; i++)
			_cleared[i] = cfg.GetValue("cleared", $"lv{i + 1}", false).AsBool();
		_unlocked = Mathf.Clamp(cfg.GetValue("progress", "unlocked", 1).AsInt32(), 1, LevelCount);
		_diff = (Diff)Mathf.Clamp(cfg.GetValue("progress", "difficulty", (int)Diff.Normal).AsInt32(), 0, 2);
		_slots = Mathf.Clamp(cfg.GetValue("progress", "slots", DefaultSlots).AsInt32(), MinSlots, MaxSlots);
		_levelIndex = Mathf.Clamp(cfg.GetValue("progress", "level", 1).AsInt32() - 1, 0, LevelCount - 1);
	}

	private void SaveProgress()
	{
		var cfg = new ConfigFile();
		for (int i = 0; i < LevelCount; i++)
			cfg.SetValue("cleared", $"lv{i + 1}", _cleared[i]);
		cfg.SetValue("progress", "unlocked", _unlocked);
		cfg.SetValue("progress", "difficulty", (int)_diff);
		cfg.SetValue("progress", "slots", _slots);
		cfg.SetValue("progress", "level", _levelIndex + 1);
		var err = cfg.Save(ProgressPath);
		if (err == Error.Ok)
			GD.Print($"[Sheep] progress saved: unlocked={_unlocked} level={_levelIndex + 1} " +
					 $"diff={DiffNames[(int)_diff]} slots={_slots} cleared=[{ClearedMarks()}]");
		else
			GD.PushError($"[Sheep] save progress failed: {err}");
	}

	private string ClearedMarks()
	{
		var parts = new List<string>();
		for (int i = 0; i < LevelCount; i++)
			parts.Add(_cleared[i] ? "1" : "0");
		return string.Join("", parts);
	}

	// ===================== 生成：几何 =====================

	/// <summary>
	/// 一张方块的全部数据。<b>纯数据，不是节点</b> —— 生成算法只跟它打交道，
	/// 于是「这一关生不生成得出来、可不可解」可以在没有任何画面的情况下跑校验。
	/// </summary>
	private sealed class TileData
	{
		public int TypeIdx = -1;
		public int Layer;
		public Vector2 Pos;     // 中心点（像素）
		public float Size;      // 边长（像素）
	}

	/// <summary>本关实际的层表：基础层表 ± 难度的层数增减。</summary>
	private static List<(int C, int R)> ResolveLayers(int levelIndex, Diff diff)
	{
		var list = new List<(int, int)>(Levels[levelIndex].Layers);
		int delta = DiffLayerDelta(diff);
		for (int i = 0; i < delta; i++)
		{
			var (c, r) = list[^1];
			list.Add((Mathf.Max(2, c - 1), Mathf.Max(2, r - 1)));
		}
		for (int i = 0; i < -delta && list.Count > 2; i++)
			list.RemoveAt(list.Count - 1);
		return list;
	}

	/// <summary>这一关「大概多少块、几种图案」。真实数量会因为补 3 的倍数差 0~2 块，所以只能说大概。</summary>
	private static string DescribeLevel(int levelIndex, Diff diff)
	{
		var ls = ResolveLayers(levelIndex, diff);
		int total = 0;
		foreach (var (c, r) in ls)
			total += c * r;
		int holes = Mathf.Min(Levels[levelIndex].Holes, Mathf.Max(0, total - 3));
		int genCap = Levels[levelIndex].GenCap + DiffCapDelta(diff);
		return $"{ls.Count} 层 · {Levels[levelIndex].Types} 种图案 · 约 {total - holes} 块 · 交错 {genCap}";
	}

	/// <summary>
	/// 摆出这一关的堆叠：每一层的格子、层与层之间的错位、然后随机挖几个洞。
	///
	/// 层与层之间的错位有讲究：要让「下层的每一张都被上层压住」，相邻两层必须错开半格。
	/// 尺寸缩水的那一步必须往正方向错（错反了最边上那张就露出来了），
	/// 尺寸没变的那一步则来回摆——一直往同一个方向错，塔会越飘越偏，最后飘出屏幕。
	/// 挖洞是故意的：不挖洞的话只有最顶上一层能点，玩家几乎没有选择余地。
	/// </summary>
	private List<TileData> BuildGeometry(int seed)
	{
		var rng = new System.Random(seed);
		_layers.Clear();
		_layers.AddRange(ResolveLayers(_levelIndex, _diff));
		int n = _layers.Count;

		// ① 每一层相对基准的错位（单位 = 格子边长）
		var offX = new float[n];
		var offY = new float[n];
		float ox = 0f, oy = 0f;
		for (int k = 0; k < n; k++)
		{
			offX[k] = ox;
			offY[k] = oy;
			if (k == n - 1)
				break;
			int dc = _layers[k].C - _layers[k + 1].C;
			int dr = _layers[k].R - _layers[k + 1].R;
			ox += dc > 0 ? 0.5f : (ox > 0f ? -0.5f : 0.5f);
			oy += dr > 0 ? 0.5f : (oy > 0f ? -0.5f : 0.5f);
		}

		// ② 先按「1 格 = 1 单位」摆一遍，量出整体多大，再回填格子边长。
		//    这样大关卡自动用小一点的方块，不用给每一关手写尺寸。
		var cells = new List<(int L, int I, int J)>();
		for (int L = 0; L < n; L++)
		{
			var (c, r) = _layers[L];
			for (int i = 0; i < c; i++)
				for (int j = 0; j < r; j++)
					cells.Add((L, i, j));
		}
		if (cells.Count < 3)
			return new List<TileData>();

		// ③ 挖洞：只从非底层挖（挖底层的洞，玩家看不见，白挖）
		int holes = Mathf.Clamp(Levels[_levelIndex].Holes, 0, Mathf.Max(0, cells.Count - 3));
		for (int i = 0; i < holes; i++)
		{
			var pick = PickRandomCell(cells, rng, 1, int.MaxValue);
			if (pick.L < 0)
				break;
			cells.Remove(pick);
		}

		// ④ 补到 3 的倍数：差 0~2 块时从最底层拿走。
		//    底层被上面压着，拿走既看不出来，也不会让任何一张变得可点（可点与否只看上面有没有东西）。
		while (cells.Count % 3 != 0)
		{
			var pick = PickRandomCell(cells, rng, 0, 0);
			if (pick.L < 0)
				pick = PickRandomCell(cells, rng, 0, int.MaxValue);
			if (pick.L < 0)
				break;
			cells.Remove(pick);
		}

		// ⑤ 换算成像素坐标：先把整体量出来，再居中放到棋盘区域里
		float minX = float.MaxValue, maxX = float.MinValue;
		float minY = float.MaxValue, maxY = float.MinValue;
		foreach (var (L, i, j) in cells)
		{
			var (c, r) = _layers[L];
			float x = offX[L] + i - (c - 1) * 0.5f;
			float y = offY[L] + j - (r - 1) * 0.5f;
			minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
			minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
		}
		float spanX = maxX - minX + 1f;
		float spanY = maxY - minY + 1f;
		_tileSize = Mathf.Clamp(Mathf.Floor(Mathf.Min(BoardW / spanX, BoardH / spanY)), TileMin, TileMax);
		float midX = (minX + maxX) * 0.5f;
		float midY = (minY + maxY) * 0.5f;

		var tiles = new List<TileData>(cells.Count);
		foreach (var (L, i, j) in cells)
		{
			var (c, r) = _layers[L];
			tiles.Add(new TileData
			{
				Layer = L,
				// 所有方块**一样大**：堆叠形状只由层表决定，可见性交给「书页叠边」（见 StackLip）。
				Size = _tileSize,
				Pos = new Vector2(
					BoardCx + (offX[L] + i - (c - 1) * 0.5f - midX) * _tileSize,
					BoardCy + (offY[L] + j - (r - 1) * 0.5f - midY) * _tileSize),
			});
		}
		return tiles;
	}

	/// <summary>随机挑一个指定层范围内的格子（空集返回 L = -1）。用蓄水池抽样，一趟扫完。</summary>
	private static (int L, int I, int J) PickRandomCell(List<(int L, int I, int J)> cells,
		System.Random rng, int minLayer, int maxLayer)
	{
		int seen = 0;
		(int L, int I, int J) pick = (-1, 0, 0);
		foreach (var c in cells)
		{
			if (c.L < minLayer || c.L > maxLayer)
				continue;
			seen++;
			if (rng.Next(seen) == 0)
				pick = c;
		}
		return pick;
	}

	// ===================== 生成：发牌（保证可解）=====================

	/// <summary>两张方块是不是上下压在一起（只有层号不同才谈得上「压」；同层不会重叠）。</summary>
	private static bool Overlaps(Vector2 pa, float sa, Vector2 pb, float sb)
	{
		float w = (sa + sb) * 0.5f - 0.5f;
		return Mathf.Abs(pa.X - pb.X) < w && Mathf.Abs(pa.Y - pb.Y) < w;
	}

	/// <summary>
	/// 给每张方块发图案 —— 这是整套「保证可解」的核心。
	///
	/// 原版羊了个羊被吐槽的地方，是堆叠方式可以用数学证明「怎么点都过不了」。
	/// 这里反过来做：<b>先造一条合法的通关路线，再沿这条路线发牌</b>。
	/// <list type="number">
	/// <item>反复从「当前没被压住的方块」里随机挑一张拿走，记为路线 R。
	///       这一步永远做得到——有限个方块里总有最上面那张，所以 R 一定存在；</item>
	/// <item>沿着 R 走，每一步决定「起一组新的同款」还是「把某一组补齐」，
	///       并且保证卡槽里没消掉的张数始终不超过 <paramref name="genCap"/>；</item>
	/// <item>一组凑满 3 张，这 3 张图案相同。</item>
	/// </list>
	///
	/// 于是「按 R 的顺序点」就是一条真·通关路线：每张被点的时候都没被压住，
	/// 卡槽占用也始终 ≤ genCap，而 genCap 又被夹在「用户选的卡槽数 - 1」以内，
	/// 所以卡槽在整条路线上永远不会被塞满。自测会把这条路线照真实规则重放一遍
	/// （见 <see cref="ReplayRoute"/>），赢了才算这一把生成成功。
	///
	/// 另外两个不变式，用来保证「不会提前意外消掉」：
	/// 同一时刻最多只有一个「开了没补齐的组」是某种图案，且一组里最多放 2 张。
	/// 否则两张不同组的同款图案凑在槽里会提前触发消除，路线就废了。
	/// </summary>
	/// <returns>成功时返回那条通关路线（方块下标序列），失败返回 null。</returns>
	private static int[]? GenerateBoard(List<TileData> tiles, int genCap, int seed, int types)
	{
		var rng = new System.Random(seed);
		int n = tiles.Count;
		if (n < 3 || n % 3 != 0)
			return null;
		int groups = n / 3;
		types = Mathf.Clamp(types, 1, TypeCount);

		// 「谁压着谁」：covers[a] = 被 a 压住的那些方块；coverCount[b] = 现在还压着 b 的有几张
		var covers = new List<int>[n];
		var coverCount = new int[n];
		for (int i = 0; i < n; i++)
			covers[i] = new List<int>();
		for (int a = 0; a < n; a++)
			for (int b = 0; b < n; b++)
			{
				if (a == b || tiles[a].Layer <= tiles[b].Layer ||
					!Overlaps(tiles[a].Pos, tiles[a].Size, tiles[b].Pos, tiles[b].Size))
					continue;
				covers[a].Add(b);
				coverCount[b]++;
			}

		// ① 随机通关路线：每一步从「没被压住的」里随机挑一张拿走，拿走后把它压着的解封
		var alive = new bool[n];
		for (int i = 0; i < n; i++)
			alive[i] = true;
		var route = new int[n];
		for (int step = 0; step < n; step++)
		{
			int pick = -1, seen = 0;
			for (int i = 0; i < n; i++)
			{
				if (!alive[i] || coverCount[i] != 0)
					continue;
				seen++;
				if (rng.Next(seen) == 0)
					pick = i;
			}
			if (pick < 0)
				return null;   // 理论上到不了这里
			route[step] = pick;
			alive[pick] = false;
			foreach (int b in covers[pick])
				coverCount[b]--;
		}

		// ② 分图案：每种先保底 1 组（这样「这一关设定用到的图案」全部都会露面，不会有哪种从头到尾没出现），
		//    剩下的组随机摊给任意图案，最后整体打乱——
		//    免得出现「前半堆全是草、后半堆全是石头」这种一眼看穿的发牌。
		var quota = new int[types];
		int rest = groups;
		if (groups >= types)
		{
			for (int t = 0; t < types; t++)
				quota[t] = 1;
			rest = groups - types;
		}
		for (int g = 0; g < rest; g++)
			quota[rng.Next(types)]++;
		for (int i = quota.Length - 1; i > 0; i--)
		{
			int j = rng.Next(i + 1);
			(quota[i], quota[j]) = (quota[j], quota[i]);
		}

		// ③ 沿路线发牌
		var open = new List<int>();      // 已开组、还没凑齐的图案（互不相同）
		var counts = new List<int>();    // 对应的张数（1 或 2）
		int held = 0;                    // 此刻卡槽里没消掉的张数 = counts 之和
		for (int step = 0; step < n; step++)
		{
			int completeIdx = PickWhere(counts, 2, rng);   // 补齐 → 凑满 3 张立刻消掉
			int growIdx = PickWhere(counts, 1, rng);       // 已经有 1 张的组再加 1 张
			bool canComplete = completeIdx >= 0;
			bool canGrow = growIdx >= 0 && held + 1 <= genCap;
			// 「开新组」会把占用顶到 genCap，如果这时场上没有任何一组是 2 张，
			// 下一步就动不了了（加任何一张都会超编）。所以给它加一道闸。
			bool canStart = HasQuota(quota, open) && held + 1 <= genCap &&
							(held + 1 < genCap || CountWhere(counts, 2) >= 1);

			int move;   // 0 = 补齐一组 / 1 = 给某组加一张 / 2 = 开一组新的
			if (canComplete && (held >= genCap || (!canGrow && !canStart) || rng.NextDouble() < 0.25))
				move = 0;
			else if (canGrow && canStart)
				move = rng.Next(2) == 0 ? 1 : 2;   // 注意别写成 rng.Next(2)：那是 0/1，0 会被当成「补齐」
			else if (canGrow)
				move = 1;
			else if (canStart)
				move = 2;
			else if (canComplete)
				move = 0;
			else
				return null;

			int tile = route[step];
			if (move == 0)
			{
				tiles[tile].TypeIdx = open[completeIdx];
				open.RemoveAt(completeIdx);
				counts.RemoveAt(completeIdx);
				held -= 2;
			}
			else if (move == 1)
			{
				tiles[tile].TypeIdx = open[growIdx];
				counts[growIdx] = 2;
				held += 1;
			}
			else
			{
				int t = PickNewType(quota, open, rng);
				if (t < 0)
					return null;
				quota[t]--;
				open.Add(t);
				counts.Add(1);
				held += 1;
				tiles[tile].TypeIdx = t;
			}
		}
		return open.Count == 0 && held == 0 ? route : null;
	}

	/// <summary>还有额度、且当前没有「开着的同款组」的图案里随机挑一个（蓄水池抽样）。</summary>
	private static int PickNewType(int[] quota, List<int> open, System.Random rng)
	{
		int seen = 0, pick = -1;
		for (int k = 0; k < quota.Length; k++)
		{
			if (quota[k] <= 0 || open.Contains(k))
				continue;
			seen++;
			if (rng.Next(seen) == 0)
				pick = k;
		}
		return pick;
	}

	private static bool HasQuota(int[] quota, List<int> open)
	{
		for (int t = 0; t < quota.Length; t++)
			if (quota[t] > 0 && !open.Contains(t))
				return true;
		return false;
	}

	private static int CountWhere(List<int> list, int v)
	{
		int c = 0;
		foreach (int x in list)
			if (x == v)
				c++;
		return c;
	}

	private static int PickWhere(List<int> list, int v, System.Random rng)
	{
		int seen = 0, pick = -1;
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i] != v)
				continue;
			seen++;
			if (rng.Next(seen) == 0)
				pick = i;
		}
		return pick;
	}

	// ===================== 开局 / 重开 =====================

	/// <summary>
	/// 开一局。<paramref name="seed"/> 传 0 表示「随便来一个」——
	/// 「每次进关卡都要随机」这条要求就落在这里，所以默认走的是当前时间。
	/// 传具体种子则生成完全一样的一局（自测要反复复现同一个盘面时用）。
	/// </summary>
	private void StartLevel(int levelIndex, Diff diff, int slots, int seed)
	{
		_levelIndex = Mathf.Clamp(levelIndex, 0, LevelCount - 1);
		_diff = diff;
		_slots = Mathf.Clamp(slots, MinSlots, MaxSlots);
		// 生成时的交错程度必须 ≤ 卡槽数 - 1，否则「构造出来的那条通关路线」自己就会把卡槽塞满
		_genCap = Mathf.Clamp(Levels[_levelIndex].GenCap + DiffCapDelta(_diff), 3, _slots - 1);
		_seed = seed != 0 ? seed : (int)(Time.GetTicksUsec() & 0x7fffffff);

		ClearBoard();
		_types = Levels[_levelIndex].Types;
		var data = BuildGeometry(_seed);
		var route = GenerateBoard(data, _genCap, _seed, _types);
		if (route == null)
		{
			// 兜底：生成器自带可解性构造，正常永远走不到。真走到了就换个种子重来一次。
			GD.PushError($"[Sheep] generate failed (seed={_seed}), retrying");
			_seed += 977;
			data = BuildGeometry(_seed);
			route = GenerateBoard(data, _genCap, _seed, _types);
		}
		if (route == null || data.Count == 0)
		{
			GD.PushError("[Sheep] generate failed twice, giving up");
			return;
		}
		_route = route;

		for (int i = 0; i < data.Count; i++)
		{
			var d = data[i];
			var tile = new Tile
			{
				Index = i,
				TypeIdx = d.TypeIdx,
				Layer = d.Layer,
				SizePx = d.Size,
				HomePos = d.Pos,
				Position = d.Pos,
				ZAsRelative = false,   // 层号用绝对的，免得被父节点的 z 叠上去
				ZIndex = d.Layer,
			};
			_board.AddChild(tile);
			_tiles.Add(tile);
		}
		_inPile = data.Count;
		ComputeCover();
		ComputeVisibility();

		_phase = Phase.Play;
		_slotFrames.Visible = true;
		_slotTiles.Visible = true;
		_setup.Visible = false;
		_dim.Visible = false;
		// 「选关」必须重新显示出来：ShowSetup() 会把它藏掉，
		// 而进游戏之后玩家还要能随时回去换关卡（不然只能靠结算面板里的按钮）
		_setupButton.Visible = true;
		_restartButton.Visible = true;

		Relayout();
		RefreshFreeState();
		UpdateInfo();

		GD.Print($"[Sheep] level {_levelIndex + 1} \"{Levels[_levelIndex].Name}\" " +
				 $"diff={DiffNames[(int)_diff]} slots={_slots} genCap={_genCap} types={_types} " +
				 $"tiles={data.Count} groups={data.Count / 3} tilePx={_tileSize} seed={_seed} " +
				 $"hidden={_hiddenTiles} (top2 {_hiddenTop}) badges={_badgedTiles} " +
				 $"minVisible={_minSamples}/25");
	}

	private void ClearBoard()
	{
		foreach (var t in _tiles)
		{
			if (GodotObject.IsInstanceValid(t))
				t.QueueFree();
		}
		_tiles.Clear();
		_slots_.Clear();
		_inPile = 0;
		_clearedTriples = 0;
		_picks = 0;
		_route = System.Array.Empty<int>();
		_covers = System.Array.Empty<List<int>>();
		_hiders = System.Array.Empty<List<int>>();
		_coverCount = System.Array.Empty<int>();
		_hiddenTiles = 0;
		_hiddenTop = 0;
		_badgedTiles = 0;
		_coveredTiles = 0;
		_minSamples = 25;
	}

	/// <summary>
	/// 「谁压着谁」的运行时版本。
	/// 生成器也算了同一套东西，但那边算的是纯数据；这里是给点击判可用用的，
	/// 而且会在方块被拿走时<b>递减</b>计数，所以判「点不点得动」是 O(1) 的。
	/// 顺便把反过来的那张表（<c>_hiders[b]</c> = 盖住 b 的那些牌）也建好，可见性统计要用。
	/// </summary>
	private void ComputeCover()
	{
		int n = _tiles.Count;
		_covers = new List<int>[n];
		_hiders = new List<int>[n];
		_coverCount = new int[n];
		for (int i = 0; i < n; i++)
		{
			_covers[i] = new List<int>();
			_hiders[i] = new List<int>();
		}
		for (int a = 0; a < n; a++)
			for (int b = 0; b < n; b++)
			{
				if (a == b || _tiles[a].Layer <= _tiles[b].Layer)
					continue;
				if (!Overlaps(_tiles[a].Position, _tiles[a].SizePx, _tiles[b].Position, _tiles[b].SizePx))
					continue;
				_covers[a].Add(b);
				_hiders[b].Add(a);
				_coverCount[b]++;
			}
	}

	/// <summary>
	/// 算每张牌「露出多少」（5×5 个采样点，被任何一张更上层的牌盖住就算这点看不见），
	/// 并把「整张被盖住」的那些牌记到「盖住它的、最上层且离它最近」的那张牌头上
	/// （<see cref="Tile.HiddenBelow"/>）—— 那张牌会在画面上画出书页一样的叠边（见 <c>Tile._Draw</c>）。
	///
	/// 为什么会有「整张被盖住」：同一层的方块是紧挨着的，上层的覆盖范围就是一张**无缝的毯子**，
	/// 错开半格时上下完全重合（1 对 1 盖满），错开四分之一时四张各盖一角（4 张拼满）。
	/// 这跟原版一样，是这种堆叠方式的固有结果，不是 bug；要做的是<b>让它看得出来</b>。
	/// 所以下面跑两段互相独立的推导，自测会断言它们对得上：
	/// <list type="number">
	/// <item><b>采样法</b>：25 个点全被盖住才算「整张被盖住」；</item>
	/// <item><b>记账法</b>：每张被整张盖住的牌都记到某张牌头上 → 求和。</item>
	/// </list>
	/// </summary>
	private void ComputeVisibility()
	{
		int n = _tiles.Count;
		_hiddenTiles = 0;
		_hiddenTop = 0;
		_badgedTiles = 0;
		_minSamples = 25;
		_coveredTiles = 0;
		for (int i = 0; i < n; i++)
		{
			_tiles[i].VisibleSamples = 0;
			_tiles[i].HiddenBelow = 0;
		}

		// ① 采样：每张牌露出几个点
		for (int q = 0; q < n; q++)
		{
			int seen = SamplesVisible(q);
			_tiles[q].VisibleSamples = seen;
			_minSamples = Mathf.Min(_minSamples, seen);
			if (_hiders[q].Count > 0)
				_coveredTiles++;
		}

		// ② 记账：整张被盖住的，记到「最上层、离它最近」的那张牌头上 —— 那张牌会画出书页叠边。
		//    不限层数：只要出现「两张及以上完全重叠」，就要有书页堆叠的效果。
		//    （第 3 层往下被整张盖住本身是正常的，不用动布局；这里只是让它在画面上看得出来。）
		int needLayer = Mathf.Max(0, _layers.Count - 2);
		for (int q = 0; q < n; q++)
		{
			if (_tiles[q].VisibleSamples != 0 || _hiders[q].Count == 0)
				continue;
			_hiddenTiles++;
			if (_tiles[q].Layer >= needLayer)
				_hiddenTop++;   // 顺带记一下「上面两层」有多少张，只用于日志
			int best = -1;
			float bestD = float.MaxValue;
			foreach (int p in _hiders[q])
			{
				float d = _tiles[p].Position.DistanceSquaredTo(_tiles[q].Position);
				if (best < 0 || _tiles[p].Layer > _tiles[best].Layer ||
					(_tiles[p].Layer == _tiles[best].Layer && d < bestD))
				{
					best = p;
					bestD = d;
				}
			}
			if (best >= 0)
				_tiles[best].HiddenBelow++;
		}
		foreach (var t in _tiles)
			if (t.HiddenBelow > 0)
				_badgedTiles++;

	}

	/// <summary>这张牌露着几个采样点（5×5 个点，全被更上层的牌盖住 = 整张看不见）。</summary>
	private int SamplesVisible(int q)
	{
		var t = _tiles[q];
		float half = t.SizePx * 0.5f * 0.98f;
		int seen = 0;
		for (int i = 0; i < 5; i++)
			for (int j = 0; j < 5; j++)
			{
				var pt = t.Position + new Vector2(
					(i / 4f - 0.5f) * 2f * half,
					(j / 4f - 0.5f) * 2f * half);
				bool covered = false;
				foreach (int p in _hiders[q])
				{
					var u = _tiles[p];
					float ph = u.SizePx * 0.5f;
					if (Mathf.Abs(pt.X - u.Position.X) <= ph &&
						Mathf.Abs(pt.Y - u.Position.Y) <= ph)
					{
						covered = true;
						break;
					}
				}
				if (!covered)
					seen++;
			}
		return seen;
	}

	// ===================== 规则 =====================

	/// <summary>这张方块能不能点：还压在堆里，且身上没有任何一张。</summary>
	private bool IsFree(Tile tile)
	{
		return !tile.InSlot && !tile.Cleared && _coverCount[tile.Index] == 0;
	}

	/// <summary>棋盘上还剩几张（不含已经被消掉的）。</summary>
	private int RemainingInPile => _inPile;

	/// <summary>卡槽里现在占了几格。</summary>
	private int SlotUsed => _slots_.Count;

	/// <summary>
	/// 点一张方块：能点就送进卡槽，然后结算消除、判定胜负。
	/// 所有规则都收在这一个入口里（自测也走这里，不另开旁路）。
	/// </summary>
	private bool PickTile(Tile tile)
	{
		if (_phase != Phase.Play || tile.InSlot || tile.Cleared)
			return false;
		if (!IsFree(tile))
		{
			Nudge(tile);
			ShowToast("这张还被压着，点不动");
			PlaySfx(0.5f, -12f);
			return false;
		}

		_picks++;
		MoveToSlot(tile);
		ResolveMatches();
		UpdateInfo();

		if (_inPile == 0)
		{
			if (_slots_.Count == 0)
				Win();
			else
				Lose("牌都翻完了，卡槽里还剩着对不上的");
		}
		else if (_slots_.Count >= _slots)
		{
			Lose("卡槽塞满了");
		}
		return true;
	}

	private void MoveToSlot(Tile tile)
	{
		// 从堆里拿走 → 它压着的那些解封（覆盖计数递减）
		foreach (int b in _covers[tile.Index])
			_coverCount[b]--;
		_inPile--;

		tile.InSlot = true;
		_slots_.Add(tile);
		// 从 Board 挪到卡槽那一层：z 从「层号」变成「槽位顺序」
		tile.GetParent()?.RemoveChild(tile);
		tile.ZIndex = tile.Index;   // 同一格里的顺序，随便一个单调值即可
		_slotTiles.AddChild(tile);

		PlaySfx(0.9f + _slots_.Count * 0.04f, -9f);
		RelayoutSlots();
		RefreshFreeState();
	}

	/// <summary>卡槽里凑齐 3 张同图案就一起消掉。用 while 兜一层连锁（正常不会触发）。</summary>
	private void ResolveMatches()
	{
		bool again = true;
		while (again)
		{
			again = false;
			for (int a = 0; a < _slots_.Count && !again; a++)
				for (int b = a + 1; b < _slots_.Count && !again; b++)
					for (int c = b + 1; c < _slots_.Count && !again; c++)
					{
						int t = _slots_[a].TypeIdx;
						if (_slots_[b].TypeIdx != t || _slots_[c].TypeIdx != t)
							continue;
						ClearTriple(_slots_[a], _slots_[b], _slots_[c]);
						again = true;
					}
		}
	}

	private void ClearTriple(Tile a, Tile b, Tile c)
	{
		foreach (var t in new[] { a, b, c })
		{
			_slots_.Remove(t);
			t.Cleared = true;
			PopAndHide(t);
		}
		_clearedTriples++;
		PlaySfx(1.35f, -5f);
		RelayoutSlots();
	}

	/// <summary>消掉时的收场动画：先弹一下再消失（纯装饰，不影响任何判定）。</summary>
	private void PopAndHide(Tile t)
	{
		t.ZIndex = 500;
		var tw = CreateTween();
		tw.SetParallel(true);
		tw.TweenProperty(t, "scale", Vector2.One * 1.3f, 0.12);
		tw.TweenProperty(t, "modulate:a", 0f, 0.18);
		tw.Chain().TweenCallback(Callable.From(() =>
		{
			if (GodotObject.IsInstanceValid(t))
				t.Visible = false;
		}));
	}

	/// <summary>「被压着点不动」的反馈：左右抖一下。</summary>
	private void Nudge(Tile tile)
	{
		tile.Position = tile.HomePos;
		float x = tile.HomePos.X;
		var tw = CreateTween();
		tw.TweenProperty(tile, "position:x", x + 11f, 0.05);
		tw.TweenProperty(tile, "position:x", x - 9f, 0.06);
		tw.TweenProperty(tile, "position:x", x, 0.06);
	}

	/// <summary>
	/// 刷新「哪些能点」的视觉：被压住的整体压暗一档。
	///
	/// 调暗的幅度是个平衡：太暗 → 下面那些牌明明露着大半却认不出图案，
	/// 玩家就没法规划；太亮 → 分不清哪张能点。0.70 时被压住的牌图案仍然清楚，
	/// 而和全亮的可点牌一比就能一眼分开。
	/// </summary>
	private void RefreshFreeState()
	{
		foreach (var t in _tiles)
		{
			if (!GodotObject.IsInstanceValid(t) || t.InSlot || t.Cleared)
				continue;
			t.Modulate = _coverCount[t.Index] == 0 ? Colors.White : new Color(0.70f, 0.68f, 0.64f);
		}
	}

	// ===================== 胜负 =====================

	private void Win()
	{
		_phase = Phase.Win;
		bool first = !_cleared[_levelIndex];
		_cleared[_levelIndex] = true;
		if (_levelIndex + 1 >= _unlocked && _levelIndex + 1 < LevelCount)
			_unlocked = _levelIndex + 2;
		SaveProgress();

		bool hasNext = _levelIndex + 1 < LevelCount;
		_nextButton.Visible = hasNext;
		ShowOverlay("通关！",
			$"第 {_levelIndex + 1} 关「{Levels[_levelIndex].Name}」 · " +
			$"{DiffNames[(int)_diff]} · {_slots} 格卡槽\n共 {_tiles.Count} 块，点了 {_picks} 次",
			first ? "★ 首次通关" : "这关之前已经过了",
			"继续挑战下一关吧");
		PlaySfx(1.5f, -3f);
		GD.Print($"[Sheep] WIN level {_levelIndex + 1} picks={_picks} unlocked={_unlocked}");
	}

	private void Lose(string reason)
	{
		_phase = Phase.Lose;
		_nextButton.Visible = false;   // 没通关，就别摆一个「下一关」在那儿
		ShowOverlay("卡住了",
			$"第 {_levelIndex + 1} 关「{Levels[_levelIndex].Name}」 · {DiffNames[(int)_diff]}\n" +
			$"还剩 {_inPile} 块没翻",
			reason,
			"再来一局会重新随机一个新的堆叠");
		PlaySfx(0.42f, -6f);
		GD.Print($"[Sheep] LOSE level {_levelIndex + 1}: {reason} pileLeft={_inPile} slot={_slots_.Count}/{_slots}");
	}

	// ===================== 界面 =====================

	private void BuildTopBar()
	{
		_topBar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_topBar.OffsetLeft = 20;
		_topBar.OffsetTop = 20;
		_topBar.OffsetRight = -20;
		_topBar.OffsetBottom = 100;
		_topBar.AddThemeConstantOverride("separation", 14);

		GameArt.StyleButton(_homeButton, new Color("#8f7bff"), Colors.White, fontSize: 32);
		_homeButton.CustomMinimumSize = new Vector2(140, 80);
		_homeButton.Text = "返回";
		_homeButton.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);

		GameArt.StyleButton(_setupButton, new Color("#4fa8ff"), Colors.White, fontSize: 32);
		_setupButton.CustomMinimumSize = new Vector2(140, 80);
		_setupButton.Text = "选关";
		_setupButton.Pressed += ShowSetup;

		GameArt.StyleButton(_restartButton, new Color("#ff8f6b"), Colors.White, fontSize: 32);
		_restartButton.CustomMinimumSize = new Vector2(140, 80);
		_restartButton.Text = "重开";
		_restartButton.Pressed += () => StartLevel(_levelIndex, _diff, _slots, 0);
	}

	private void BuildInfo()
	{
		_info = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		GameArt.OutlineText(_info, Colors.White, 28, 7);
		_info.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_info.GrowHorizontal = GrowDirection.Both;
		_info.OffsetTop = 108;
		_info.OffsetBottom = 158;
		_ui.AddChild(_info);
	}

	private void BuildToast()
	{
		_toast = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
			Visible = false,
		};
		GameArt.OutlineText(_toast, Colors.White, 34, 8);
		_toast.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
		_toast.GrowHorizontal = GrowDirection.Both;
		_toast.GrowVertical = GrowDirection.End;
		// 不放在屏幕正中：正中是棋盘，提示会挡住正在点的方块
		_toast.OffsetTop = 860f;
		_toast.OffsetBottom = 860f;
		_ui.AddChild(_toast);
	}

	/// <summary>
	/// 选关面板：难度 + 关卡 + 卡槽数量。
	/// 三组都是「一排小按钮」，选中态用同一套配色规则（见 <see cref="StyleChip"/>），
	/// 所以加一档难度、加一关，只要往表里加数据就行。
	/// </summary>
	private void BuildSetup()
	{
		_setup = new Control { MouseFilter = MouseFilterEnum.Ignore };
		_setup.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_setup);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(1f, 0.98f, 0.92f, 0.97f), 34, new Color("#b98a4e"));
		box.ContentMarginLeft = box.ContentMarginRight = 30;
		box.ContentMarginTop = box.ContentMarginBottom = 26;
		panel.AddThemeStyleboxOverride("panel", box);
		_setup.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 12);
		panel.AddChild(col);

		var title = new Label { Text = "羊了个羊", HorizontalAlignment = HorizontalAlignment.Center };
		title.AddThemeFontSizeOverride("font_size", 62);
		title.AddThemeColorOverride("font_color", new Color("#7a4a12"));
		col.AddChild(title);

		var sub = new Label
		{
			Text = "每局都是新摆的堆叠 · 三张同款即消 · 卡槽塞满就输",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		sub.AddThemeFontSizeOverride("font_size", 21);
		sub.AddThemeColorOverride("font_color", new Color(0.45f, 0.36f, 0.22f));
		col.AddChild(sub);

		col.AddChild(MakeSectionLabel("难易程度"));
		var diffRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		diffRow.AddThemeConstantOverride("separation", 12);
		col.AddChild(diffRow);
		for (int i = 0; i < DiffNames.Length; i++)
		{
			int idx = i;
			var b = new Button { CustomMinimumSize = new Vector2(196, 76) };
			b.Pressed += () =>
			{
				_diff = (Diff)idx;
				SaveProgress();
				RefreshSetup();
				PlayPop(b);
				GD.Print($"[Sheep] difficulty -> {DiffNames[idx]}");
			};
			diffRow.AddChild(b);
			_diffButtons[i] = b;
		}

		col.AddChild(MakeSectionLabel("选择关卡（通关后解锁下一关）"));
		var levelRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		levelRow.AddThemeConstantOverride("separation", 8);
		col.AddChild(levelRow);
		for (int i = 0; i < LevelCount; i++)
		{
			int idx = i;
			var b = new Button { CustomMinimumSize = new Vector2(124, 118) };
			var name = new Label
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				MouseFilter = MouseFilterEnum.Ignore,
			};
			name.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			name.GrowHorizontal = GrowDirection.Both;
			name.GrowVertical = GrowDirection.Both;
			name.AddThemeFontSizeOverride("font_size", 24);
			b.AddChild(name);

			var mark = new Label
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Bottom,
				MouseFilter = MouseFilterEnum.Ignore,
			};
			mark.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
			mark.GrowHorizontal = GrowDirection.Both;
			mark.GrowVertical = GrowDirection.Begin;
			mark.OffsetTop = -34;
			mark.OffsetBottom = -8;
			mark.AddThemeFontSizeOverride("font_size", 19);
			b.AddChild(mark);

			b.Pressed += () =>
			{
				_levelIndex = idx;
				SaveProgress();
				RefreshSetup();
				PlayPop(b);
				GD.Print($"[Sheep] level -> {idx + 1}");
			};
			levelRow.AddChild(b);
			_levelButtons[i] = b;
			_levelNames[i] = name;
			_levelMarks[i] = mark;
		}

		col.AddChild(MakeSectionLabel($"卡槽数量（默认 {DefaultSlots}）"));
		var slotRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		slotRow.AddThemeConstantOverride("separation", 14);
		col.AddChild(slotRow);
		for (int n = MinSlots; n <= MaxSlots; n++)
		{
			int val = n;
			var b = new Button { CustomMinimumSize = new Vector2(96, 72) };
			b.Pressed += () =>
			{
				_slots = val;
				SaveProgress();
				RefreshSetup();
				PlayPop(b);
				GD.Print($"[Sheep] slots -> {val}");
			};
			slotRow.AddChild(b);
			_slotButtons[n - MinSlots] = b;
		}

		_hintLabel = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		_hintLabel.AddThemeFontSizeOverride("font_size", 21);
		_hintLabel.AddThemeColorOverride("font_color", new Color(0.42f, 0.34f, 0.20f));
		_hintLabel.CustomMinimumSize = new Vector2(600, 0);
		col.AddChild(_hintLabel);

		_startButton = new Button
		{
			Text = "开 始",
			CustomMinimumSize = new Vector2(320, 96),
			SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
		};
		GameArt.StyleButton(_startButton, new Color("#5cb85c"), Colors.White, 26, new Color("#317231"), 42);
		_startButton.Pressed += () =>
		{
			PlayPop(_startButton);
			StartLevel(_levelIndex, _diff, _slots, 0);
		};
		col.AddChild(_startButton);
	}

	private Label MakeSectionLabel(string text)
	{
		var lb = new Label
		{
			Text = text,
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		lb.AddThemeFontSizeOverride("font_size", 22);
		lb.AddThemeColorOverride("font_color", new Color("#8a6a3a"));
		return lb;
	}

	/// <summary>一排「小方块按钮」的统一样式：选中 = 橙底白字，未选中 = 白底棕字。</summary>
	private static void StyleChip(Button b, bool selected, int fontSize)
	{
		Color bg = selected ? new Color("#e8a33d") : new Color(1f, 1f, 1f, 0.94f);
		Color fg = selected ? Colors.White : new Color("#6b4a1f");
		Color border = selected ? new Color("#9c6218") : new Color("#cbb28a");
		GameArt.StyleButton(b, bg, fg, 20f, border, fontSize);
		b.AddThemeStyleboxOverride("disabled",
			GameArt.MakeBox(new Color(0.86f, 0.85f, 0.81f, 0.8f), 20f, new Color("#b8b4aa")));
		b.AddThemeColorOverride("font_disabled_color", new Color(0.55f, 0.54f, 0.51f));
	}

	private void ShowSetup()
	{
		_phase = Phase.Setup;
		ClearBoard();
		_setup.Visible = true;
		_dim.Visible = false;
		_slotFrames.Visible = false;
		_slotTiles.Visible = false;
		_setupButton.Visible = false;
		_restartButton.Visible = false;
		_info.Text = "";
		_toast.Visible = false;
		RefreshSetup();
		GD.Print($"[Sheep] setup. unlocked={_unlocked} cleared=[{ClearedMarks()}]");
	}

	private void RefreshSetup()
	{
		for (int i = 0; i < LevelCount; i++)
		{
			bool locked = i + 1 > _unlocked;
			_levelButtons[i].Disabled = locked;
			_levelNames[i].Text = $"第 {i + 1} 关\n{Levels[i].Name}";
			_levelMarks[i].Text = locked ? "未解锁" : (_cleared[i] ? "已通关 ★" : "未通关");
			StyleChip(_levelButtons[i], i == _levelIndex, 24);
			// 关名和状态要自己上色：Button 的字体颜色不会自动传给子 Label
			_levelNames[i].AddThemeColorOverride("font_color", locked
				? new Color(0.55f, 0.54f, 0.51f)
				: (i == _levelIndex ? Colors.White : new Color("#6b4a1f")));
			_levelMarks[i].AddThemeColorOverride("font_color", locked
				? new Color(0.62f, 0.60f, 0.57f)
				: (i == _levelIndex ? new Color("#fff0d0") : new Color("#9c7a3c")));
		}
		for (int i = 0; i < DiffNames.Length; i++)
		{
			_diffButtons[i].Text = DiffNames[i];
			StyleChip(_diffButtons[i], (int)_diff == i, 30);
		}
		for (int i = 0; i < _slotButtons.Length; i++)
		{
			_slotButtons[i].Text = (MinSlots + i).ToString();
			StyleChip(_slotButtons[i], _slots == MinSlots + i, 30);
		}
		_hintLabel.Text = $"{DescribeLevel(_levelIndex, _diff)}　｜　{DiffDesc[(int)_diff]}\n" +
						  $"卡槽 {_slots} 格 · 已通关 {ClearedCount()} / {LevelCount} 关";
	}

	private int ClearedCount()
	{
		int c = 0;
		foreach (bool b in _cleared)
			if (b)
				c++;
		return c;
	}

	private void BuildOverlay()
	{
		_dim = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.62f),
			MouseFilter = MouseFilterEnum.Stop,   // 挡住底下的点击，结算/选关时不能操作棋盘
			Visible = false,
			ZIndex = 5,                            // 相对 _ui 的 1000 → 1005，压在提示条之上
		};
		_dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_dim);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(1f, 0.98f, 0.92f, 0.98f), 30, new Color("#b98a4e"));
		box.ContentMarginLeft = box.ContentMarginRight = 40;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		_dim.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 14);
		panel.AddChild(col);

		_overTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(_overTitle, new Color("#7a4a12"), 58, 6);
		_overTitle.AddThemeColorOverride("font_outline_color", new Color(1f, 0.95f, 0.8f, 0.9f));
		col.AddChild(_overTitle);

		_overInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_overInfo.AddThemeFontSizeOverride("font_size", 26);
		_overInfo.AddThemeColorOverride("font_color", new Color(0.32f, 0.26f, 0.16f));
		col.AddChild(_overInfo);

		_overExtra = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_overExtra.AddThemeFontSizeOverride("font_size", 24);
		_overExtra.AddThemeColorOverride("font_color", new Color("#c2701a"));
		col.AddChild(_overExtra);

		_overFooter = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			CustomMinimumSize = new Vector2(520, 0),
		};
		_overFooter.AddThemeFontSizeOverride("font_size", 20);
		_overFooter.AddThemeColorOverride("font_color", new Color(0.5f, 0.44f, 0.34f));
		col.AddChild(_overFooter);

		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 16);
		col.AddChild(row);

		_nextButton = MakeOverlayButton("下一关", new Color("#5cb85c"));
		_nextButton.Pressed += () =>
		{
			if (_levelIndex + 1 < LevelCount)
				StartLevel(_levelIndex + 1, _diff, _slots, 0);
		};
		row.AddChild(_nextButton);

		_retryButton = MakeOverlayButton("再来一局", new Color("#ff8f6b"));
		_retryButton.Pressed += () => StartLevel(_levelIndex, _diff, _slots, 0);
		row.AddChild(_retryButton);

		_toSetupButton = MakeOverlayButton("重选关卡", new Color("#8f7bff"));
		_toSetupButton.Pressed += ShowSetup;
		row.AddChild(_toSetupButton);
	}

	private Button MakeOverlayButton(string text, Color bg)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(196, 92) };
		GameArt.StyleButton(b, bg, Colors.White, 22, fontSize: 32);
		return b;
	}

	private void ShowOverlay(string title, string info, string extra, string footer)
	{
		// 结算面板要独占视线：提示条的 z 在 _ui 里默认 0，虽然被 _dim 压住，
		// 但留一条正在淡出的文字在面板边缘也不好看，直接收掉。
		_toastTween?.Kill();
		_toast.Visible = false;

		_overTitle.Text = title;
		_overInfo.Text = info;
		_overExtra.Text = extra;
		_overExtra.Visible = extra.Length > 0;
		_overFooter.Text = footer;
		_dim.Visible = true;
	}

	// ===================== 布局：棋盘与卡槽 =====================

	/// <summary>把所有方块按当前层号从下往上摆回棋盘，再排一次卡槽。</summary>
	private void Relayout()
	{
		foreach (var t in _tiles)
		{
			if (!GodotObject.IsInstanceValid(t) || t.InSlot || t.Cleared)
				continue;
			t.Position = t.HomePos;
		}
		RelayoutSlots();
	}

	/// <summary>
	/// 卡槽那一行：卡槽格子数会变（6~10），所以格子宽度是按可用宽度反算的，
	/// 免得选了 10 格就溢出屏幕。
	/// </summary>
	private void RelayoutSlots()
	{
		int n = _slots;
		_slotTile = Mathf.Floor(Mathf.Min(SlotTileMax, (SlotMaxW - SlotGap * (n - 1)) / n));
		_slotTile = Mathf.Max(_slotTile, SlotTileMin);
		float totalW = _slotTile * n + SlotGap * (n - 1);
		float x0 = BoardCx - totalW * 0.5f + _slotTile * 0.5f;

		for (int i = 0; i < _slots_.Count; i++)
		{
			var t = _slots_[i];
			t.SizePx = _slotTile;
			t.Position = new Vector2(x0 + i * (_slotTile + SlotGap), SlotCy);
			t.ZIndex = t.Index;
		}
		_slotFrames.QueueRedraw();
	}

	private void UpdateInfo()
	{
		if (_phase != Phase.Play)
			return;
		// 注意别写成 $"{_slots_}"：那是个 List，会原样打印出类名（踩过）
		_info.Text = $"第 {_levelIndex + 1} 关「{Levels[_levelIndex].Name}」· {DiffNames[(int)_diff]} · " +
					 $"卡槽 {SlotUsed}/{_slots} · 剩 {_inPile}";
	}

	// ===================== 提示 / 音效 =====================

	/// <summary>3 个播放器轮流用：连点时同一瞬间可以叠几个声音，不会互相打断。</summary>
	private void BuildSfxPool()
	{
		var stream = GD.Load<AudioStream>("res://sfx/ding.wav");
		for (int i = 0; i < 3; i++)
		{
			var p = new AudioStreamPlayer { Stream = stream };
			AddChild(p);
			_sfxPool.Add(p);
		}
	}

	private void ShowToast(string msg)
	{
		_toastTween?.Kill();
		_toast.Text = msg;
		_toast.Visible = true;
		_toast.Modulate = Colors.White;
		_toastTween = CreateTween();
		_toastTween.TweenInterval(0.9);
		_toastTween.TweenProperty(_toast, "modulate:a", 0f, 0.3);
		_toastTween.TweenCallback(Callable.From(() => _toast.Visible = false));
	}

	private void PlaySfx(float pitch, float volumeDb)
	{
		if (_sfxPool.Count == 0)
			return;
		_sfxIndex = (_sfxIndex + 1) % _sfxPool.Count;
		var p = _sfxPool[_sfxIndex];
		p.PitchScale = pitch;
		p.VolumeDb = volumeDb;
		p.Play();
	}

	private void PlayPop(Node node)
	{
		if (node is not Control c || c.Size.X <= 0f)
			return;
		c.PivotOffset = c.Size * 0.5f;
		var tw = CreateTween();
		tw.TweenProperty(c, "scale", new Vector2(0.93f, 0.93f), 0.06);
		tw.TweenProperty(c, "scale", Vector2.One, 0.16)
			.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
	}

	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Sheep] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	// ===================== 输入 =====================

	/// <summary>
	/// 点一下就翻一张。桌面和手机都要能用。
	///
	/// 坑：项目同时开了 <c>emulate_touch_from_mouse</c> 和默认的 <c>emulate_mouse_from_touch</c>，
	/// 所以桌面上一次点击会**同时**产生一个鼠标按下和一个触摸按下。
	/// 两边都处理的话一张牌会被点两次（第二次还会被当成「点空」），
	/// 这里用「同一位置 60ms 内只认一次」去重——比按事件类型分叉稳，手机上也一样有效。
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		Vector2 pos;
		switch (@event)
		{
			case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left && mb.Pressed:
				pos = mb.Position;
				break;
			case InputEventScreenTouch st when st.Pressed:
				pos = st.Position;
				break;
			default:
				return;
		}

		ulong now = Time.GetTicksMsec();
		if (now - _lastPressMs < 60 && pos.DistanceTo(_lastPressPos) < 8f)
			return;
		_lastPressMs = now;
		_lastPressPos = pos;
		HandleTap(pos);
	}

	private void HandleTap(Vector2 pos)
	{
		if (_phase != Phase.Play || _setup.Visible)
			return;
		var tile = TopTileAt(pos);
		if (tile != null)
			PickTile(tile);
	}

	/// <summary>
	/// 这个点上是哪一张：按层号从高到低找<b>画在这个像素上的那一张</b>。
	///
	/// 注意找的是「眼睛看到的是谁」，而不是「有没有能点的」：
	/// 点在被压住的方块上就应该什么都不发生（顶多抖一下），
	/// 否则会变成「隔着上面那张去点下面那张」，跟画面完全对不上。
	/// </summary>
	private Tile? TopTileAt(Vector2 pos)
	{
		Tile? best = null;
		foreach (var t in _tiles)
		{
			if (!GodotObject.IsInstanceValid(t) || t.InSlot || t.Cleared || !t.Visible)
				continue;
			float h = t.SizePx * 0.5f;
			if (Mathf.Abs(pos.X - t.Position.X) > h || Mathf.Abs(pos.Y - t.Position.Y) > h)
				continue;
			if (best == null || t.Layer > best.Layer)
				best = t;
		}
		return best;
	}

	// ===================== 卡槽底框 =====================

	/// <summary>卡槽底框由这个哑节点画：它只知道去问 Host 要数据。</summary>
	private sealed partial class SlotFramePainter : Node2D
	{
		public SheepGame Host = null!;

		public override void _Draw() => Host.DrawSlotFrames(this);
	}

	private void DrawSlotFrames(CanvasItem ci)
	{
		int n = _slots;
		float size = Mathf.Floor(Mathf.Min(SlotTileMax, (SlotMaxW - SlotGap * (n - 1)) / n));
		size = Mathf.Max(size, SlotTileMin);
		float totalW = size * n + SlotGap * (n - 1);
		float x0 = BoardCx - totalW * 0.5f + size * 0.5f;
		float h = size * 0.5f;

		// 先铺一条底衬，让卡槽区在花花绿绿的背景上一眼能认出来
		float pad = 16f;
		ci.DrawStyleBox(Icons.SlotBar,
			new Rect2(x0 - h - pad, SlotCy - h - pad, totalW + pad * 2f, size + pad * 2f));

		for (int i = 0; i < n; i++)
		{
			var c = new Vector2(x0 + i * (size + SlotGap), SlotCy);
			ci.DrawStyleBox(Icons.SlotEmpty, new Rect2(c.X - h, c.Y - h, size, size));
		}
	}

	// ===================== 画面上的节点 =====================
	//
	// 照旧是「纯数据 + 自己画自己」的哑节点：位置和状态由外面的规则改，节点只负责 _Draw。
	// 每个继承 Godot 节点的类都必须 partial，否则编译报 GD0001。

	/// <summary>
	/// 一张方块。原点取在方块<b>中心</b>，
	/// 这样命中判定（比绝对值）和压盖判定（比绝对值）都只要一个减法，不用再换坐标。
	/// </summary>
	private sealed partial class Tile : Node2D
	{
		public int Index;            // 在 _tiles 里的下标（压盖用）
		public int TypeIdx;          // 图案
		public int Layer;            // 层号，同时就是棋盘上的 z
		public float SizePx = 88f;
		public Vector2 HomePos;      // 在棋盘上的位置（抖动动画要还原到这儿）
		public bool InSlot;
		public bool Cleared;
		public int VisibleSamples = 25;  // 25 个采样点里露出来几个（统计与自测用）
		public int HiddenBelow;          // 我下面藏着几张整张看不见的牌（只算顶上两层）→ 画书页叠边

		public override void _Draw()
		{
			float h = SizePx * 0.5f;
			bool onBoard = !InSlot && !Cleared;

			// 书页叠边：这一张把下面那张**整张盖住**了，就在右下方画出错开的一两道边，
			// 看上去就是一叠摞起来的书页。正好只出现在「两张完全重合」的地方。
			// （位置挪不动，只能画——原因写在 StackLip 的注释里。）
			if (onBoard && HiddenBelow > 0)
			{
				float d = Mathf.Max(2f, SizePx * StackLip);
				int k = Mathf.Min(HiddenBelow, 3);
				for (; k >= 1; k--)
					DrawStyleBox(Icons.StackEdge, new Rect2(-h + d * k, -h + d * k, SizePx, SizePx));
			}

			DrawStyleBox(Icons.TileBox, new Rect2(-h, -h, SizePx, SizePx));
			Icons.Draw(this, TypeIdx, Vector2.Zero, h * 0.62f);
		}
	}

	/// <summary>背景装饰：太阳、几朵云、底下的两层草坡。不参与任何判定。</summary>
	private sealed partial class SceneryView : Node2D
	{
		public override void _Draw()
		{
			DrawCircle(new Vector2(596f, 168f), 68f, new Color(1f, 0.93f, 0.52f, 0.9f));
			DrawCircle(new Vector2(596f, 168f), 96f, new Color(1f, 0.95f, 0.62f, 0.22f));

			Cloud(new Vector2(150f, 186f), 1f);
			Cloud(new Vector2(452f, 108f), 0.7f);
			Cloud(new Vector2(268f, 320f), 0.56f);

			Hill(1074f, new Color(0.44f, 0.68f, 0.37f, 0.65f), 1f);
			Hill(1152f, new Color(0.31f, 0.55f, 0.29f, 0.7f), 1.7f);
		}

		private void Cloud(Vector2 c, float s)
		{
			var col = new Color(1f, 1f, 1f, 0.72f);
			DrawCircle(c + new Vector2(-44f, 6f) * s, 30f * s, col);
			DrawCircle(c + new Vector2(44f, 10f) * s, 26f * s, col);
			DrawCircle(c + new Vector2(-6f, -14f) * s, 36f * s, col);
			DrawCircle(c + new Vector2(20f, 2f) * s, 28f * s, col);
		}

		/// <summary>用一串竖条拼一条起伏的草坡：比手搓多边形简单，远看也够用。</summary>
		private void Hill(float baseY, Color col, float phase)
		{
			for (int x = -24; x < 744; x += 20)
			{
				float t = (x + 20) * 0.0125f * phase;
				float y = baseY - Mathf.Sin(t) * 26f - Mathf.Sin(t * 0.41f) * 32f;
				DrawRect(new Rect2(x, y, 21f, 1320f - y), col);
			}
		}
	}

	// ===================== 图案（16 种，全部现画）=====================
	//
	// 和原版一样是 16 种：草、胡萝卜、玉米、木头、羊毛、水滴、火焰、剪刀、
	// 手套、水桶、线团、瓶子、铃铛、蘑菇、叶子、石头。
	// 全部用 DrawXxx 画出来，不需要任何素材文件。
	// 画的时候统一用「以方块中心为原点、半径 1 的单位坐标」，
	// 外面传进来的 s 就是单位长度，因此同一份画法能同时用在棋盘（大）和卡槽（小）上。

	private static class Icons
	{
		/// <summary>棋盘上的方块底：奶油色 + 棕色描边。</summary>
		public static readonly StyleBoxFlat TileBox = MakeBox(new Color("#fdf6e4"), new Color("#a9773c"), 12, 4);

		/// <summary>卡槽空位：半透明，让玩家随时看清还剩几格。</summary>
		public static readonly StyleBoxFlat SlotEmpty = MakeBox(new Color(1f, 1f, 1f, 0.16f), new Color(1f, 1f, 1f, 0.45f), 10, 3);

		/// <summary>卡槽整条的底衬。</summary>
		public static readonly StyleBoxFlat SlotBar = MakeBox(new Color(0.22f, 0.16f, 0.07f, 0.34f), new Color(1f, 1f, 1f, 0.14f), 22, 3);

		/// <summary>书页叠边：露在下面那几张牌边缘上的一道浅棕卡片边。</summary>
		public static readonly StyleBoxFlat StackEdge =
			MakeBox(new Color("#e3cda4"), new Color("#a9773c"), 12, 3);

		private static StyleBoxFlat MakeBox(Color bg, Color border, int radius, int width)
		{
			var sb = new StyleBoxFlat { BgColor = bg };
			sb.CornerRadiusTopLeft = sb.CornerRadiusTopRight = radius;
			sb.CornerRadiusBottomLeft = sb.CornerRadiusBottomRight = radius;
			sb.BorderColor = border;
			sb.BorderWidthLeft = sb.BorderWidthRight = sb.BorderWidthTop = sb.BorderWidthBottom = width;
			return sb;
		}

		/// <summary>单位坐标 → 屏幕坐标。</summary>
		private static Vector2 P(Vector2 c, float s, float x, float y) => new(c.X + x * s, c.Y + y * s);

		private static Vector2[] Poly(Vector2 c, float s, params float[] xy)
		{
			var a = new Vector2[xy.Length / 2];
			for (int i = 0; i < a.Length; i++)
				a[i] = P(c, s, xy[i * 2], xy[i * 2 + 1]);
			return a;
		}

		/// <summary>半个圆 + 一条平底（蘑菇伞盖用）。从左边扫到右边，角度 PI → 2PI 是屏幕上方。</summary>
		private static Vector2[] Dome(Vector2 c, float s, float cx, float cy, float rx, float ry)
		{
			var pts = new Vector2[17];
			for (int i = 0; i <= 16; i++)
			{
				float a = Mathf.Pi + Mathf.Pi * i / 16f;
				pts[i] = P(c, s, cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry);
			}
			return pts;
		}

		public static void Draw(CanvasItem ci, int type, Vector2 c, float s)
		{
			switch (type)
			{
				case 0: Grass(ci, c, s); break;
				case 1: Carrot(ci, c, s); break;
				case 2: Corn(ci, c, s); break;
				case 3: Wood(ci, c, s); break;
				case 4: Wool(ci, c, s); break;
				case 5: Water(ci, c, s); break;
				case 6: Fire(ci, c, s); break;
				case 7: Scissors(ci, c, s); break;
				case 8: Glove(ci, c, s); break;
				case 9: Bucket(ci, c, s); break;
				case 10: Yarn(ci, c, s); break;
				case 11: Bottle(ci, c, s); break;
				case 12: Bell(ci, c, s); break;
				case 13: Mushroom(ci, c, s); break;
				case 14: Leaf(ci, c, s); break;
				default: Stone(ci, c, s); break;
			}
		}

		private static readonly Color Line = new("#3a2a16");
		private static readonly Color White = new("#ffffff");

		// 0 草
		private static void Grass(CanvasItem ci, Vector2 c, float s)
		{
			var green = new Color("#63b84a");
			for (int i = -1; i <= 1; i++)
			{
				float tilt = i * 0.72f;
				GameArt.Poly(ci, Poly(c, s,
					tilt * 0.3f - 0.15f, 0.9f,
					tilt * 0.3f + 0.15f, 0.9f,
					tilt, -0.92f), green, Line, s * 0.09f);
			}
			ci.DrawRect(new Rect2(c.X - s * 0.66f, c.Y + s * 0.84f, s * 1.32f, s * 0.18f), new Color("#4a6b2a"));
		}

		// 1 胡萝卜
		private static void Carrot(CanvasItem ci, Vector2 c, float s)
		{
			var orange = new Color("#f2872e");
			var leaf = new Color("#5aa832");
			GameArt.Poly(ci, Poly(c, s, -0.16f, -0.16f, -0.6f, -0.95f, -0.04f, -0.62f), leaf, Line, s * 0.07f);
			GameArt.Poly(ci, Poly(c, s, 0.02f, -0.2f, 0.0f, -1.0f, 0.3f, -0.6f), leaf, Line, s * 0.07f);
			GameArt.Poly(ci, Poly(c, s, 0.16f, -0.16f, 0.6f, -0.95f, 0.04f, -0.62f), leaf, Line, s * 0.07f);
			GameArt.Poly(ci, Poly(c, s, -0.46f, -0.1f, 0.46f, -0.1f, 0f, 1.0f), orange, Line, s * 0.09f);
			for (int i = 0; i < 3; i++)
			{
				float y = 0.08f + i * 0.24f;
				float w = 0.32f - i * 0.09f;
				ci.DrawLine(P(c, s, -w, y), P(c, s, w, y), new Color("#a8531a"), s * 0.06f);
			}
		}

		// 2 玉米
		private static void Corn(CanvasItem ci, Vector2 c, float s)
		{
			var yellow = new Color("#f6c343");
			var husk = new Color("#6cb43f");
			GameArt.Poly(ci, Poly(c, s, -0.18f, 0.16f, -0.86f, 0.72f, -0.52f, 0.98f, -0.12f, 0.5f), husk, Line, s * 0.07f);
			GameArt.Poly(ci, Poly(c, s, 0.18f, 0.16f, 0.86f, 0.72f, 0.52f, 0.98f, 0.12f, 0.5f), husk, Line, s * 0.07f);
			GameArt.Ellipse(ci, P(c, s, 0f, 0.03f), s * 0.48f, s * 0.86f, yellow, Line, s * 0.08f);
			for (int r = 0; r < 4; r++)
				for (int q = -1; q <= 1; q++)
					ci.DrawCircle(P(c, s, q * 0.21f + (r % 2) * 0.09f, -0.42f + r * 0.34f), s * 0.085f,
						new Color("#e39a1e"));
		}

		// 3 木头
		private static void Wood(CanvasItem ci, Vector2 c, float s)
		{
			var brown = new Color("#b07a3f");
			ci.DrawRect(new Rect2(c.X - s * 0.85f, c.Y - s * 0.5f, s * 1.7f, s * 1.0f), brown);
			ci.DrawRect(new Rect2(c.X - s * 0.85f, c.Y - s * 0.5f, s * 1.7f, s * 1.0f), Line, false, s * 0.09f);
			GameArt.Ellipse(ci, P(c, s, 0.56f, 0f), s * 0.32f, s * 0.46f, new Color("#d8a86a"), Line, s * 0.07f);
			GameArt.Ellipse(ci, P(c, s, 0.56f, 0f), s * 0.15f, s * 0.21f, brown, Line, s * 0.06f);
			ci.DrawLine(P(c, s, -0.72f, -0.2f), P(c, s, 0.22f, -0.24f), new Color("#8a5a24"), s * 0.06f);
			ci.DrawLine(P(c, s, -0.72f, 0.18f), P(c, s, 0.26f, 0.16f), new Color("#8a5a24"), s * 0.06f);
		}

		// 4 羊毛
		private static void Wool(CanvasItem ci, Vector2 c, float s)
		{
			var puff = new Color("#fbf7ef");
			ci.DrawCircle(P(c, s, 0f, -0.3f), s * 0.5f, puff);
			ci.DrawCircle(P(c, s, -0.42f, 0.12f), s * 0.42f, puff);
			ci.DrawCircle(P(c, s, 0.42f, 0.12f), s * 0.42f, puff);
			ci.DrawCircle(P(c, s, 0f, 0.42f), s * 0.44f, puff);
			ci.DrawCircle(P(c, s, 0f, 0.05f), s * 0.34f, new Color("#6f5847"));
			ci.DrawCircle(P(c, s, -0.13f, 0f), s * 0.07f, Line);
			ci.DrawCircle(P(c, s, 0.13f, 0f), s * 0.07f, Line);
			ci.DrawArc(P(c, s, 0f, 0.05f), s * 0.34f, 0, Mathf.Tau, 20, Line, s * 0.07f, true);
			ci.DrawArc(P(c, s, 0f, 0.05f), s * 0.46f, 0, Mathf.Tau, 22, new Color("#c9c2b4"), s * 0.06f, true);
		}

		// 5 水滴
		private static void Water(CanvasItem ci, Vector2 c, float s)
		{
			GameArt.Poly(ci, Poly(c, s,
				0f, -1.0f, 0.34f, -0.34f, 0.62f, 0.24f, 0.5f, 0.72f, 0.24f, 0.94f,
				-0.24f, 0.94f, -0.5f, 0.72f, -0.62f, 0.24f, -0.34f, -0.34f),
				new Color("#3fa9f5"), Line, s * 0.08f);
			ci.DrawCircle(P(c, s, -0.18f, 0.36f), s * 0.15f, new Color(1f, 1f, 1f, 0.7f));
		}

		// 6 火焰
		private static void Fire(CanvasItem ci, Vector2 c, float s)
		{
			GameArt.Poly(ci, Poly(c, s,
				0f, -1.0f, 0.34f, -0.5f, 0.66f, -0.06f, 0.6f, 0.48f, 0.3f, 0.9f,
				-0.3f, 0.9f, -0.6f, 0.48f, -0.66f, -0.06f, -0.34f, -0.5f),
				new Color("#f0522f"), Line, s * 0.08f);
			GameArt.Poly(ci, Poly(c, s,
				0f, -0.26f, 0.3f, 0.12f, 0.34f, 0.52f, 0f, 0.86f, -0.34f, 0.52f, -0.3f, 0.12f),
				new Color("#ffd24a"), new Color("#c98a12"), s * 0.05f);
		}

		// 7 剪刀
		private static void Scissors(CanvasItem ci, Vector2 c, float s)
		{
			var steel = new Color("#b9c2cc");
			var dark = new Color("#5a6472");
			GameArt.Poly(ci, Poly(c, s, -0.6f, -0.95f, -0.34f, -0.86f, 0.42f, 0.22f, 0.24f, 0.36f), steel, dark, s * 0.07f);
			GameArt.Poly(ci, Poly(c, s, 0.6f, -0.95f, 0.34f, -0.86f, -0.42f, 0.22f, -0.24f, 0.36f), steel, dark, s * 0.07f);
			ci.DrawArc(P(c, s, -0.4f, 0.62f), s * 0.28f, 0, Mathf.Tau, 18, dark, s * 0.11f, true);
			ci.DrawArc(P(c, s, 0.4f, 0.62f), s * 0.28f, 0, Mathf.Tau, 18, dark, s * 0.11f, true);
			ci.DrawCircle(P(c, s, 0f, 0.03f), s * 0.11f, dark);
		}

		// 8 手套（连指手套：三根指头 + 一个拇指）
		private static void Glove(CanvasItem ci, Vector2 c, float s)
		{
			var tan = new Color("#e0a96d");
			GameArt.Poly(ci, Poly(c, s,
				-0.52f, 1.0f, -0.52f, -0.28f, -0.52f, -0.82f, -0.28f, -0.82f, -0.28f, -0.28f,
				-0.16f, -0.28f, -0.16f, -0.94f, 0.08f, -0.94f, 0.08f, -0.28f,
				0.2f, -0.28f, 0.2f, -0.82f, 0.44f, -0.82f, 0.44f, -0.28f,
				0.52f, -0.28f, 0.52f, 1.0f), tan, Line, s * 0.08f);
			ci.DrawCircle(P(c, s, -0.66f, 0.24f), s * 0.24f, tan);
			ci.DrawArc(P(c, s, -0.66f, 0.24f), s * 0.24f, 0, Mathf.Tau, 16, Line, s * 0.08f, true);
			ci.DrawRect(new Rect2(c.X - s * 0.6f, c.Y + s * 0.78f, s * 1.2f, s * 0.22f), new Color("#c8894a"));
			ci.DrawRect(new Rect2(c.X - s * 0.6f, c.Y + s * 0.78f, s * 1.2f, s * 0.22f), Line, false, s * 0.07f);
		}

		// 9 水桶
		private static void Bucket(CanvasItem ci, Vector2 c, float s)
		{
			var metal = new Color("#4fb3c8");
			var dark = new Color("#1d6b7d");
			ci.DrawArc(P(c, s, 0f, -0.3f), s * 0.6f, Mathf.Pi, Mathf.Tau, 20, dark, s * 0.09f, true);
			GameArt.Poly(ci, Poly(c, s, -0.62f, -0.3f, 0.62f, -0.3f, 0.44f, 0.95f, -0.44f, 0.95f),
				metal, dark, s * 0.08f);
			ci.DrawRect(new Rect2(c.X - s * 0.72f, c.Y - s * 0.44f, s * 1.44f, s * 0.2f), new Color("#9fdbe8"));
			ci.DrawRect(new Rect2(c.X - s * 0.72f, c.Y - s * 0.44f, s * 1.44f, s * 0.2f), dark, false, s * 0.07f);
			ci.DrawLine(P(c, s, -0.5f, 0.34f), P(c, s, 0.5f, 0.34f), new Color(1f, 1f, 1f, 0.55f), s * 0.07f);
		}

		// 10 线团
		private static void Yarn(CanvasItem ci, Vector2 c, float s)
		{
			var pink = new Color("#ef6ba8");
			var dark = new Color("#9c2a63");
			var light = new Color("#ffb3d4");
			ci.DrawCircle(P(c, s, 0f, 0.04f), s * 0.78f, pink);
			ci.DrawArc(P(c, s, 0f, 0.04f), s * 0.78f, 0, Mathf.Tau, 26, dark, s * 0.08f, true);
			ci.DrawArc(P(c, s, -0.22f, -0.16f), s * 0.34f, 0, Mathf.Tau, 18, light, s * 0.09f, true);
			ci.DrawArc(P(c, s, 0.26f, 0.14f), s * 0.3f, 0, Mathf.Tau, 18, light, s * 0.09f, true);
			ci.DrawArc(P(c, s, -0.04f, 0.44f), s * 0.28f, 0, Mathf.Tau, 18, light, s * 0.09f, true);
			ci.DrawArc(P(c, s, 0.86f, 0.86f), s * 0.26f, Mathf.Pi * 1.05f, Mathf.Tau * 0.98f, 12, dark, s * 0.07f, true);
		}

		// 11 瓶子
		private static void Bottle(CanvasItem ci, Vector2 c, float s)
		{
			var glass = new Color("#5fc9a0");
			var dark = new Color("#1f7a5a");
			ci.DrawRect(new Rect2(c.X - s * 0.46f, c.Y - s * 0.2f, s * 0.92f, s * 1.15f), glass);
			ci.DrawRect(new Rect2(c.X - s * 0.46f, c.Y - s * 0.2f, s * 0.92f, s * 1.15f), dark, false, s * 0.08f);
			ci.DrawRect(new Rect2(c.X - s * 0.18f, c.Y - s * 0.78f, s * 0.36f, s * 0.62f), glass);
			ci.DrawRect(new Rect2(c.X - s * 0.18f, c.Y - s * 0.78f, s * 0.36f, s * 0.62f), dark, false, s * 0.07f);
			ci.DrawRect(new Rect2(c.X - s * 0.24f, c.Y - s * 1.02f, s * 0.48f, s * 0.3f), new Color("#c99a5b"));
			ci.DrawRect(new Rect2(c.X - s * 0.24f, c.Y - s * 1.02f, s * 0.48f, s * 0.3f), dark, false, s * 0.07f);
			ci.DrawRect(new Rect2(c.X - s * 0.34f, c.Y + s * 0.06f, s * 0.13f, s * 0.62f), new Color(1f, 1f, 1f, 0.5f));
		}

		// 12 铃铛
		private static void Bell(CanvasItem ci, Vector2 c, float s)
		{
			var gold = new Color("#f2c14e");
			var dark = new Color("#9c6a12");
			ci.DrawArc(P(c, s, 0f, -0.72f), s * 0.17f, 0, Mathf.Tau, 14, dark, s * 0.1f, true);
			GameArt.Poly(ci, Poly(c, s,
				0f, -0.6f, 0.34f, -0.34f, 0.52f, 0.34f, 0.66f, 0.56f,
				-0.66f, 0.56f, -0.52f, 0.34f, -0.34f, -0.34f), gold, dark, s * 0.08f);
			ci.DrawRect(new Rect2(c.X - s * 0.72f, c.Y + s * 0.56f, s * 1.44f, s * 0.2f), gold);
			ci.DrawRect(new Rect2(c.X - s * 0.72f, c.Y + s * 0.56f, s * 1.44f, s * 0.2f), dark, false, s * 0.07f);
			ci.DrawCircle(P(c, s, 0f, 0.9f), s * 0.18f, dark);
		}

		// 13 蘑菇
		private static void Mushroom(CanvasItem ci, Vector2 c, float s)
		{
			var stem = new Color("#f7ecdc");
			var cap = new Color("#e0533f");
			ci.DrawRect(new Rect2(c.X - s * 0.26f, c.Y - s * 0.05f, s * 0.52f, s * 1.0f), stem);
			ci.DrawRect(new Rect2(c.X - s * 0.26f, c.Y - s * 0.05f, s * 0.52f, s * 1.0f), Line, false, s * 0.08f);
			GameArt.Poly(ci, Dome(c, s, 0f, -0.1f, 0.86f, 0.62f), cap, Line, s * 0.08f);
			ci.DrawCircle(P(c, s, -0.36f, -0.44f), s * 0.14f, White);
			ci.DrawCircle(P(c, s, 0.08f, -0.6f), s * 0.13f, White);
			ci.DrawCircle(P(c, s, 0.44f, -0.28f), s * 0.12f, White);
		}

		// 14 叶子
		private static void Leaf(CanvasItem ci, Vector2 c, float s)
		{
			var green = new Color("#7cc242");
			var dark = new Color("#3d7a17");
			var pts = new List<Vector2>(30);
			for (int i = 0; i <= 15; i++)
			{
				float t = i / 15f;
				pts.Add(P(c, s, -Mathf.Sin(Mathf.Pi * t) * 0.62f, 1f - 2f * t));
			}
			for (int i = 14; i >= 1; i--)
			{
				float t = i / 15f;
				pts.Add(P(c, s, Mathf.Sin(Mathf.Pi * t) * 0.62f, 1f - 2f * t));
			}
			GameArt.Poly(ci, pts.ToArray(), green, dark, s * 0.08f);
			ci.DrawLine(P(c, s, 0f, -0.92f), P(c, s, 0f, 0.92f), dark, s * 0.07f);
			for (int i = 0; i < 3; i++)
			{
				float y = -0.42f + i * 0.44f;
				ci.DrawLine(P(c, s, 0f, y), P(c, s, -0.32f, y + 0.24f), dark, s * 0.05f);
				ci.DrawLine(P(c, s, 0f, y), P(c, s, 0.32f, y + 0.24f), dark, s * 0.05f);
			}
		}

		// 15 石头
		private static void Stone(CanvasItem ci, Vector2 c, float s)
		{
			var gray = new Color("#9aa3b2");
			GameArt.Poly(ci, Poly(c, s,
				-0.9f, 0.32f, -0.62f, -0.5f, -0.1f, -0.78f, 0.5f, -0.62f,
				0.88f, -0.05f, 0.7f, 0.62f, 0.1f, 0.86f, -0.5f, 0.7f), gray, Line, s * 0.09f);
			GameArt.Poly(ci, Poly(c, s, -0.58f, -0.28f, -0.12f, -0.58f, 0.16f, -0.32f, -0.3f, 0.02f),
				new Color("#c8d0dd"), new Color("#7a8494"), s * 0.05f);
			ci.DrawLine(P(c, s, -0.2f, 0.24f), P(c, s, 0.2f, 0.52f), new Color("#6b7382"), s * 0.06f);
		}
	}

	// ===================== 自测 =====================
	//
	// 触发方式：项目根目录放 selftest.flag，内容写 sheep，然后启动游戏
	// （首页会立刻切到本场景，本场景看到内容是自己就跑这一套）。
	//
	// 这里最值钱的两条断言是「生成器可解」和「存档能读回来」：
	//   前者不是「看着像能过」，而是把生成器给的那条路线**照真实规则重放一遍**，
	//   用同一套消除/判负逻辑走到全消完；后者不是「我调了保存函数」，
	//   而是把文件重新读回来比对数值。

	/// <summary>
	/// 自测的入口只做一件事：把异常抓出来。
	/// 自测是「<c>_ = RunSelfTestAsync()</c>」这样放手跑的，里面抛异常会**被静默吞掉**——
	/// 上次就是靠这层包装才看见生成器在某一关炸了。
	/// </summary>
	private async Task RunSelfTestAsync()
	{
		try
		{
			await RunSelfTestBodyAsync();
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[SELFTEST] EXCEPTION: {e.GetType().Name}: {e.Message}");
			GD.PrintErr(e.StackTrace ?? "");
			GetTree().Quit(1);
		}
	}

	private async Task RunSelfTestBodyAsync()
	{
		GD.Print("[SELFTEST] begin (sheep)");
		await Wait(0.4);
		int fails = 0;

		// 自测要在「从零开始」的状态下跑，但用户自己的进度不能被它冲掉 → 先备份
		bool[] backupCleared = (bool[])_cleared.Clone();
		int backupUnlocked = _unlocked, backupDiff = (int)_diff;
		int backupSlots = _slots, backupLevel = _levelIndex;

		// ① 选关面板：默认卡槽 7、只有第 1 关是开的
		_unlocked = 1;
		for (int i = 0; i < LevelCount; i++)
			_cleared[i] = false;
		_diff = Diff.Normal;
		_slots = DefaultSlots;
		_levelIndex = 0;
		ShowSetup();
		await Wait(0.2);

		bool panelOk = _setup.Visible && _slotFrames.Visible == false && _topBar.Visible;
		GD.Print($"[SELFTEST] setup panel visible: {panelOk}");
		if (!panelOk) fails++;

		bool lockOk = !_levelButtons[0].Disabled && _levelButtons[1].Disabled &&
					  _levelButtons[4].Disabled;
		GD.Print($"[SELFTEST] level 1 open, 2~5 locked (unlocked=1): {lockOk}");
		if (!lockOk) fails++;

		bool slotChips = _slotButtons.Length == MaxSlots - MinSlots + 1 &&
						 _slotButtons[0].Text == "6" && _slotButtons[4].Text == "10";
		GD.Print($"[SELFTEST] slot chips 6~10: {slotChips}");
		if (!slotChips) fails++;

		// ② 卡槽 6 和 10 时，那一行都不能溢出屏幕
		float w6 = SlotWidthFor(6), w10 = SlotWidthFor(10);
		bool fitOk = w6 <= SlotMaxW + 0.5f && w10 <= SlotMaxW + 0.5f &&
					 SlotTileFor(10) >= SlotTileMin;
		GD.Print($"[SELFTEST] slot row fits: 6格宽 {w6:0.#} / 10格宽 {w10:0.#} (上限 {SlotMaxW}) -> {fitOk}");
		if (!fitOk) fails++;

		// ③ 生成器大样本：5 关 × 3 难度 × 8 种子，每一局都要「真能通关」
		int sweepFail = 0, minTiles = int.MaxValue, maxTiles = 0;
		var tilesPerLevel = new int[LevelCount];   // 「普通」难度下的张数，用来查难度阶梯
		const int Seeds = 8;
		for (int lv = 0; lv < LevelCount; lv++)
			for (int d = 0; d < 3; d++)
				for (int k = 0; k < Seeds; k++)
				{
					int seed = 9000 + lv * 100 + d * 10 + k;
					_levelIndex = lv;
					_diff = (Diff)d;
					var data = BuildGeometry(seed);
					int types = Levels[lv].Types;
					int genCap = Mathf.Clamp(Levels[lv].GenCap + DiffCapDelta((Diff)d), 3, DefaultSlots - 1);
					if (d == (int)Diff.Normal && k == 0)
						tilesPerLevel[lv] = data.Count;
					string why = "";
					bool ok = data.Count >= 3 && data.Count % 3 == 0;
					if (!ok)
						why = $"张数 {data.Count} 不是 3 的倍数";
					var genRoute = ok ? GenerateBoard(data, genCap, seed, types) : null;
					if (ok && genRoute == null)
					{
						ok = false;
						why = "生成器没给出路线";
					}
					else if (ok)
					{
						ok = ReplayRoute(data, genRoute!, DefaultSlots, out int mh, out why) && mh <= genCap;
						if (ok)
						{
							// 三种硬约束：
							// ① 每种图案的张数必须是 3 的倍数；
							// ② 本关设定要用到的图案，每种都至少要露面一组（不能有哪种从头到尾没出现）；
							// ③ 本关没设定的图案，一张都不许出现。
							var cnt = new int[TypeCount];
							foreach (var t in data)
								cnt[t.TypeIdx]++;
							int groups = data.Count / 3;
							for (int t = 0; t < TypeCount && ok; t++)
							{
								if (t < types)
								{
									if (cnt[t] % 3 != 0)
									{
										ok = false;
										why = $"第 {t + 1} 种图案有 {cnt[t]} 张（不是 3 的倍数）";
									}
									else if (groups >= types && cnt[t] == 0)
									{
										ok = false;
										why = $"第 {t + 1} 种图案一张都没出现";
									}
								}
								else if (cnt[t] != 0)
								{
									ok = false;
									why = $"第 {t + 1} 种图案不该出现（本关只用 {types} 种）";
								}
							}
						}
					}
					if (!ok)
					{
						sweepFail++;
						if (sweepFail <= 3)
							GD.Print($"[SELFTEST]   gen FAIL lv{lv + 1} diff{d} seed={seed}: {why}");
					}
					minTiles = Mathf.Min(minTiles, data.Count);
					maxTiles = Mathf.Max(maxTiles, data.Count);
				}
		GD.Print($"[SELFTEST] generator sweep {LevelCount * 3 * Seeds} 局: 失败 {sweepFail}, " +
				 $"方块数 {minTiles}~{maxTiles} -> {sweepFail == 0}");
		if (sweepFail != 0) fails++;

		// ③b 难度阶梯：越往后的关，张数 / 图案种类<b>都必须严格递增</b>，层数与交错<b>不减少</b>。
		//     L2/L3 层数相同，靠张数（30→45）和图案种类（8→10）继续加难。
		//     这一条是给「难度要一步步增加」这句要求兜底的——改关卡表时手滑写反了会当场红。
		bool ladder = true;
		string ladderText = "";
		for (int lv = 0; lv < LevelCount; lv++)
		{
			int layers = ResolveLayers(lv, Diff.Normal).Count;
			ladderText += $"\n            第 {lv + 1} 关 {Levels[lv].Name}: {tilesPerLevel[lv]} 块 · " +
						  $"{Levels[lv].Types} 种图案 · {layers} 层 · 交错 {Levels[lv].GenCap}";
			if (lv == 0)
				continue;
			if (tilesPerLevel[lv] <= tilesPerLevel[lv - 1] ||
				Levels[lv].Types <= Levels[lv - 1].Types ||
				layers < ResolveLayers(lv - 1, Diff.Normal).Count ||
				Levels[lv].GenCap < Levels[lv - 1].GenCap)
				ladder = false;
		}
		GD.Print($"[SELFTEST] 难度阶梯递增: {ladder}{ladderText}");
		if (!ladder) fails++;

		// ③c 堆叠几何 —— 三条硬要求：
		//     ① 所有牌必须**一样大**（不许再靠缩小上层方块来露脸）；
		//     ② 「整张被盖住」得两份独立推导对得上：采样法数出来的张数 == 记账法的总和；
		//     ③ 只要出现了「两张及以上完全重叠」，就必须有牌画上了书页叠边 —— 不限层数。
		for (int lv = 0; lv < LevelCount; lv++)
		{
			StartLevel(lv, Diff.Normal, DefaultSlots, 5000 + lv);
			float minSize = float.MaxValue, maxSize = 0f;
			int stacked = 0, clickable = 0, accounted = 0, badged = 0;
			foreach (var t in _tiles)
			{
				minSize = Mathf.Min(minSize, t.SizePx);
				maxSize = Mathf.Max(maxSize, t.SizePx);
				accounted += t.HiddenBelow;
				if (t.HiddenBelow > 0)
					badged++;
				if (_coverCount[t.Index] == 0)
					clickable++;
				if (_hiders[t.Index].Count > 0)
					stacked++;
			}
			bool ok = maxSize - minSize < 0.01f && accounted == _hiddenTiles &&
					  (_hiddenTiles == 0 || badged > 0) && stacked > 0;
			GD.Print($"[SELFTEST] 第 {lv + 1} 关牌堆: 共 {_tiles.Count} 块 · 可点 {clickable} 张 · " +
					 $"方块边长全部 {minSize:F0}px · 有牌压着 {stacked} 张 · " +
					 $"完全重叠（整张被盖住）{_hiddenTiles} 张（其中上面两层 {_hiddenTop} 张）· " +
					 $"画了书页叠边 {badged} 张（记账 {accounted}）-> {ok}");
			if (!ok) fails++;
			if (lv == 0 || lv == LevelCount - 1)
			{
				await Wait(0.35);
				SaveShot($"sheep_lv{lv + 1}");
			}
		}

		// ④ 随机性：同种子必同盘；不同种子必不同盘（「每次进关卡都要随机」）
		_levelIndex = 0;
		_diff = Diff.Normal;
		int t0 = Levels[0].Types;
		var s1 = BuildGeometry(777);
		GenerateBoard(s1, 3, 777, t0);
		var s2 = BuildGeometry(777);
		GenerateBoard(s2, 3, 777, t0);
		var s3 = BuildGeometry(778);
		GenerateBoard(s3, 3, 778, t0);
		bool sameSeed = SameLayout(s1, s2);
		bool diffSeed = !SameLayout(s1, s3);
		GD.Print($"[SELFTEST] seed 777 两次同盘: {sameSeed}; seed 778 与 777 不同盘: {diffSeed}");
		if (!sameSeed) fails++;
		if (!diffSeed) fails++;

		// ⑤ 真开局 + 点击链路（用的是和玩家完全一样的入口）
		StartLevel(0, Diff.Normal, DefaultSlots, 4242);
		await Wait(0.2);
		bool startOk = _phase == Phase.Play && _tiles.Count > 0 && _tiles.Count % 3 == 0 &&
					   _inPile == _tiles.Count && _slots_.Count == 0 && _setup.Visible == false;
		GD.Print($"[SELFTEST] level start: phase={_phase} tiles={_tiles.Count} inPile={_inPile} -> {startOk}");
		if (!startOk) fails++;

		bool bgOk = !GameArt.IsClearColor(
			GetViewport().GetTexture().GetImage().GetPixel(18, 640));
		GD.Print($"[SELFTEST] background covers screen: {bgOk}");
		if (!bgOk) fails++;

		// 被压住的方块：点上去必须没反应
		var covered = FirstCoveredTile();
		bool coveredOk = covered != null && !IsFree(covered) && !PickTile(covered!) &&
						 _slots_.Count == 0 && _inPile == _tiles.Count;
		GD.Print($"[SELFTEST] 被压住的点不动: {coveredOk}");
		if (!coveredOk) fails++;

		// 点一张能点的 → 进卡槽
		var freeTile = FindFreeTile(false);
		bool pickOk = freeTile != null && PickTile(freeTile!) &&
					  _slots_.Count == 1 && freeTile!.InSlot && freeTile.GetParent() == _slotTiles;
		GD.Print($"[SELFTEST] 点一张能点的 -> 进卡槽: {pickOk} (type={freeTile?.TypeIdx})");
		if (!pickOk) fails++;

		// 命中判定：点在「某张被压住的方块露出来的那一角」上，拿到的必须是上面那张
		var freeOne = FindFreeTile(false);
		bool hitOk = freeOne != null && TopTileAt(freeOne!.Position) == freeOne;
		GD.Print($"[SELFTEST] 命中判定取最上面那张: {hitOk}");
		if (!hitOk) fails++;

		// 凑齐三张同图案 → 自动消掉
		StartLevel(0, Diff.Normal, DefaultSlots, 4242);
		await Wait(0.15);
		int triType = FindTypeWithThreeFree();
		if (triType >= 0)
		{
			for (int i = 0; i < 3; i++)
			{
				var t = FindFreeTileOfType(triType);
				if (t != null)
					PickTile(t);
			}
		}
		bool matchOk = triType >= 0 && SlotUsed == 0 && _clearedTriples == 1;
		GD.Print($"[SELFTEST] 三张同款自动消: type={triType} 消掉 {_clearedTriples} 组, 卡槽 {SlotUsed} -> {matchOk}");
		if (!matchOk) fails++;

		// ⑥ 输：一路点不同类型的，把卡槽塞满
		StartLevel(0, Diff.Normal, MinSlots, 4243);
		await Wait(0.15);
		int guard = 0;
		while (_phase == Phase.Play && SlotUsed < _slots && guard++ < 300)
		{
			var t = FindFreeTile(true);   // 躲开会立刻凑成 3 张的图案
			if (t == null || !PickTile(t))
				break;
		}
		bool loseOk = _phase == Phase.Lose && SlotUsed == _slots && _dim.Visible &&
					  _retryButton.Visible && !_nextButton.Visible;
		GD.Print($"[SELFTEST] 塞满卡槽 -> 输: phase={_phase} slot={SlotUsed}/{_slots} -> {loseOk}");
		if (!loseOk) fails++;
		// 上面那一串点击是在同一帧里跑完的，而截图拿到的是「上一帧画完的结果」，
		// 不歇一下的话拍到的还是点之前的样子（踩过）
		await Wait(1.4);
		SaveShot("sheep_lose");

		// ⑦ 赢：把生成器给的那条路线照真实规则点一遍，必须全部消完
		StartLevel(0, Diff.Normal, DefaultSlots, 4244);
		await Wait(0.15);
		var route = (int[])_route.Clone();
		int picked = 0, wrongPicks = 0;
		for (int i = 0; i < route.Length && _phase == Phase.Play; i++)
		{
			if (PickTile(_tiles[route[i]]))
				picked++;
			else
				wrongPicks++;
		}
		bool winOk = _phase == Phase.Win && _inPile == 0 && SlotUsed == 0 &&
					 picked == route.Length && wrongPicks == 0;
		GD.Print($"[SELFTEST] 照标准答案走完 -> 赢: phase={_phase} 点了 {picked}/{route.Length} " +
				 $"被拒 {wrongPicks} 剩余 {_inPile} 卡槽 {SlotUsed} -> {winOk}");
		if (!winOk) fails++;

		bool unlockOk = _cleared[0] && _unlocked == 2 && _nextButton.Visible;
		GD.Print($"[SELFTEST] 通关解锁下一关: cleared1={_cleared[0]} unlocked={_unlocked} -> {unlockOk}");
		if (!unlockOk) fails++;
		await Wait(0.7);   // 同上：等结算面板真的画出来那一帧
		SaveShot("sheep_win");

		// ⑧ 进度真的写进文件了（重新读回来核对，而不是「我调过保存」）
		var cfg = new ConfigFile();
		bool savedOk = cfg.Load(ProgressPath) == Error.Ok &&
					   cfg.GetValue("cleared", "lv1", false).AsBool() &&
					   cfg.GetValue("progress", "unlocked", 0).AsInt32() == 2;
		GD.Print($"[SELFTEST] progress persisted: reload={savedOk}");
		if (!savedOk) fails++;

		// ⑨ 解锁之后第 2 关的按钮要变成可点
		ShowSetup();
		await Wait(0.15);
		bool unlockUiOk = !_levelButtons[1].Disabled && _levelButtons[2].Disabled;
		GD.Print($"[SELFTEST] 解锁后第 2 关可点、第 3 关仍锁: {unlockUiOk}");
		if (!unlockUiOk) fails++;
		SaveShot("sheep_setup");

		// ⑩ 中途的画面（这时候应该有可点/被压住两种深浅、卡槽里有牌）
		StartLevel(2, Diff.Hard, 9, 4245);
		await Wait(0.15);
		int shotPicks = 0;
		while (shotPicks < 5)
		{
			var t = FindFreeTile(true);
			if (t == null || !PickTile(t))
				break;
			shotPicks++;
		}
		await Wait(0.35);
		SaveShot("sheep_play");
		bool playOk = _phase == Phase.Play && SlotUsed > 0 && _inPile > 0;
		GD.Print($"[SELFTEST] 第 3 关 挑战 · 9 格卡槽: tiles={_tiles.Count} genCap={_genCap} " +
				 $"点了 {shotPicks} 张, 卡槽 {SlotUsed}/9 -> {playOk}");
		if (!playOk) fails++;

		// 收尾：把用户原本的进度放回去（自测不能吃掉玩家的存档）
		for (int i = 0; i < LevelCount; i++)
			_cleared[i] = backupCleared[i];
		_unlocked = backupUnlocked;
		_diff = (Diff)backupDiff;
		_slots = backupSlots;
		_levelIndex = backupLevel;
		SaveProgress();
		GD.Print($"[SELFTEST] 用户进度已还原: unlocked={_unlocked} slots={_slots}");

		GD.Print(fails == 0 ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} 项)");
		await Wait(0.4);
		GetTree().Quit();
	}

	/// <summary>把生成器给的那条路线按真实规则重放一遍，用来证明这一局确实可解。</summary>
	private static bool ReplayRoute(List<TileData> tiles, int[] route, int slots,
		out int maxHeld, out string why)
	{
		int n = tiles.Count;
		maxHeld = 0;
		why = "";
		var alive = new bool[n];
		var coverCount = new int[n];
		var covers = new List<int>[n];
		for (int i = 0; i < n; i++)
		{
			alive[i] = true;
			covers[i] = new List<int>();
		}
		for (int a = 0; a < n; a++)
			for (int b = 0; b < n; b++)
			{
				if (a == b || tiles[a].Layer <= tiles[b].Layer)
					continue;
				if (!Overlaps(tiles[a].Pos, tiles[a].Size, tiles[b].Pos, tiles[b].Size))
					continue;
				covers[a].Add(b);
				coverCount[b]++;
			}

		var slot = new List<int>();
		for (int step = 0; step < route.Length; step++)
		{
			int i = route[step];
			if (i < 0 || i >= n || !alive[i])
			{
				why = $"第 {step + 1} 步的方块不在堆里了";
				return false;
			}
			if (coverCount[i] != 0)
			{
				why = $"第 {step + 1} 步点了一张被压住的方块";
				return false;
			}
			alive[i] = false;
			foreach (int b in covers[i])
				coverCount[b]--;

			int t = tiles[i].TypeIdx;
			slot.Add(t);
			int c = 0;
			foreach (int x in slot)
				if (x == t)
					c++;
			if (c >= 3)
			{
				int removed = 0;
				for (int k = slot.Count - 1; k >= 0 && removed < 3; k--)
					if (slot[k] == t)
					{
						slot.RemoveAt(k);
						removed++;
					}
			}
			maxHeld = Mathf.Max(maxHeld, slot.Count);
			if (slot.Count >= slots)
			{
				why = $"第 {step + 1} 步卡槽被塞满（{slot.Count}/{slots}）";
				return false;
			}
		}
		if (slot.Count != 0)
		{
			why = $"走完了但卡槽里还剩 {slot.Count} 张";
			return false;
		}
		return true;
	}

	private static bool SameLayout(List<TileData> a, List<TileData> b)
	{
		if (a.Count != b.Count)
			return false;
		for (int i = 0; i < a.Count; i++)
			if (a[i].TypeIdx != b[i].TypeIdx || a[i].Layer != b[i].Layer ||
				!a[i].Pos.IsEqualApprox(b[i].Pos))
				return false;
		return true;
	}

	private static float SlotTileFor(int n)
	{
		return Mathf.Max(Mathf.Floor(Mathf.Min(SlotTileMax, (SlotMaxW - SlotGap * (n - 1)) / n)), SlotTileMin);
	}

	private static float SlotWidthFor(int n) => SlotTileFor(n) * n + SlotGap * (n - 1);

	private Tile? FirstCoveredTile()
	{
		foreach (var t in _tiles)
			if (GodotObject.IsInstanceValid(t) && !t.InSlot && !t.Cleared && !IsFree(t))
				return t;
		return null;
	}

	/// <summary>找一张能点的。<paramref name="avoidTriple"/> = true 时避开「槽里已有 2 张同款」的图案。</summary>
	private Tile? FindFreeTile(bool avoidTriple)
	{
		foreach (var t in _tiles)
		{
			if (!GodotObject.IsInstanceValid(t) || t.InSlot || t.Cleared || !IsFree(t))
				continue;
			if (avoidTriple)
			{
				int c = 0;
				foreach (var s in _slots_)
					if (s.TypeIdx == t.TypeIdx)
						c++;
				if (c >= 2)
					continue;
			}
			return t;
		}
		return null;
	}

	/// <summary>找出「此刻有 3 张都能点」的图案（用来验证三张同款会自动消）。</summary>
	private int FindTypeWithThreeFree()
	{
		var seen = new int[TypeCount];
		foreach (var t in _tiles)
		{
			if (!GodotObject.IsInstanceValid(t) || t.InSlot || t.Cleared || !IsFree(t))
				continue;
			seen[t.TypeIdx]++;
		}
		for (int t = 0; t < TypeCount; t++)
			if (seen[t] >= 3)
				return t;
		return -1;
	}

	private Tile? FindFreeTileOfType(int type)
	{
		foreach (var t in _tiles)
			if (GodotObject.IsInstanceValid(t) && !t.InSlot && !t.Cleared &&
				t.TypeIdx == type && IsFree(t))
				return t;
		return null;
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}
}
