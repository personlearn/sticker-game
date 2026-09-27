#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 开局托儿所 —— 数字凑十消除（首页卡片里的「数字 · 凑十」）。
///
/// 玩法：棋盘是 16×10 的随机数字方阵（每格 1~9）。玩家在棋盘上**拖出一个矩形方框**，
/// 框里还剩下的数字相加**正好等于 10** 就把它们一起消掉；不等于 10 就红闪一下、什么也不动。
/// 消掉一个数字得 1 点「智商」，限时内消得越多分越高（整盘 160 格，所以满分 160）。
///
/// 两个设计上的关键点：
/// <list type="bullet">
/// <item>
/// <b>棋盘一定可解</b>：数字不是逐个随机撒的，而是先把整个棋盘切成一块块面积 2~3 的矩形，
/// 再往每块里填一组「和 = 10」的数字（见 <see cref="GenerateBoard"/>）。
/// 纯随机填的话最后很容易剩下一堆单格 —— 单格永远凑不出 10，那就是死局。
/// 切块生成保证了「按这些块依次消完」这条通路一定存在。
/// 块也不能切大：一块 k 格的平均值只有 10/k，块越大盘子上的小数字越多（详见 <see cref="MaxLeaf"/>）。
/// </item>
/// <item>
/// <b>消掉的位置留洞</b>：数字不会下落、也不补新数字（原版就是如此，所以分数上限是 160）。
/// 方框允许跨过空洞 —— 空洞贡献 0，不必严丝合缝地只框住数字。
/// </item>
/// </list>
///
/// 结构沿用全项目约定：一个脚本 + 一个瘦场景，图形全部 _Draw 现画，不依赖任何素材文件。
/// </summary>
public partial class NurseryGame : Control
{
	// ===================== 可调参数 =====================

	private const int Rows = 16;   // 行数（和原版一致）
	private const int Cols = 10;   // 列数
	private const int Target = 10; // 框内相加的目标值

	/// <summary>
	/// 生成棋盘时，一块「答案块」最多几格。这个数字直接决定盘面的数字分布：
	/// 一块 k 格的平均值只有 10/k，所以块越大、小数字越多。
	/// 3 是能干净切完整个棋盘的最大值里最小的一个（面积 ≥ 4 的矩形总还能再切一刀，
	/// 面积 2~3 才会停），既保证可解、又不会把盘面填成一片 1。
	/// </summary>
	private const int MaxLeaf = 3;

	// 设计分辨率（project.godot 里配的 720×1280）。窗口实际像素是另一回事，
	// 自测要把逻辑坐标换算成截图像素时得用它。
	private const float DesignW = 720f;
	private const float DesignH = 1280f;

	private const float PadX = 14f;          // 棋盘左右留白
	private const float BoardTop = 252f;     // 棋盘可用区上边界（720×1280 逻辑分辨率下的绝对值）
	private const float BoardBottom = 1128f; // 棋盘可用区下边界（下面留给提示文字）
	private const float MinTile = 24f;       // 格子边长下限：再小就点不中了
	private const float MaxTile = 74f;       // 格子边长上限：再大就显得空

	private const float PopLife = 0.26f;   // 消除时那一下「缩掉」的时长
	private const float FloatLife = 0.78f; // 「+N」往上飘的时长
	private const float FailLife = 0.34f;  // 没凑成 10 时红闪的时长
	private const float ShakeDecay = 26f;  // 凑不成 10 的震屏衰减速度（px/s）

	// 引线 HUD 的几何（Stage 绝对坐标）
	private const float FuseY = 200f;
	private const float FuseLeft = 118f;
	private const float FuseRight = 620f;
	private const float FuseH = 22f;
	private const float BombX = 78f;
	private const float BombR = 27f;
	private const float FuseTextLeft = 628f; // 倒计时数字的落脚区（引线右侧）
	private const float LowTime = 10f;       // 低于这个秒数开始报警（变红 + 炸弹抖动）

	private const string BestPath = "user://nursery_best.cfg";

	// ===================== 时长档位 =====================

	/// <summary>一档时长。加一档只要往 <see cref="Times"/> 里加一条。</summary>
	private sealed class TimeDef
	{
		public string Label = "";  // 面板上显示的名字
		public string Tag = "";    // 存档里的 key（改了就丢纪录，别乱改）
		public int Seconds;
		public Color Tint = Colors.White;
	}

	private static readonly TimeDef[] Times =
	{
		new TimeDef { Label = "1 分钟", Tag = "t60", Seconds = 60, Tint = new Color("#7ee787") },
		new TimeDef { Label = "2 分钟", Tag = "t120", Seconds = 120, Tint = new Color("#8fd3ff") },
		new TimeDef { Label = "3 分钟", Tag = "t180", Seconds = 180, Tint = new Color("#ffd77a") },
	};

	// ===================== 棋盘数据 =====================

	private enum Phase { Setup, Play, Over }

	/// <summary>
	/// 棋盘上的一块矩形（格子坐标，不是像素）。生成棋盘时用它做切块，
	/// 顺手把结果留下来当「标准答案」——自测靠它验证棋盘一定可解。
	/// </summary>
	private readonly struct Region
	{
		public readonly int C, R, W, H;

		public Region(int c, int r, int w, int h)
		{
			C = c; R = r; W = w; H = h;
		}

		public int Area => W * H;
	}

	private int[] _cells = System.Array.Empty<int>(); // 0 = 已消除，1~9 = 数字
	private readonly List<Region> _solution = new();

	private Phase _phase = Phase.Setup;
	private int _timeIndex;
	private float _totalTime;  // 本局总时长（秒）
	private float _remain;     // 剩余秒数
	private int _score;        // 智商 = 消掉的数字个数
	private int _cleared;      // 同上，语义上「已消格数」和「得分」是同一个数，分开存是为了读代码时清楚

	private int _seed;         // 0 = 按时间随机；自测里固定成一个值好复现
	private System.Random _rng = new();

	private readonly int[] _best = new int[Times.Length]; // 各档时长的最高智商，0 = 还没玩过

	// ===================== 运行时状态（动画 / 输入） =====================

	private sealed class Pop { public int Index; public int Value; public float Age; }
	private sealed class Ring { public Vector2 Pos; public float Age; public float MaxR; public Color Tint = new Color("#ffd77a"); }
	private sealed class Float { public Vector2 Pos; public string Text = ""; public Color Tint = Colors.White; public float Age; }
	private sealed class Fail { public int C0, R0, C1, R1; public float Age; }

	private readonly List<Pop> _pops = new();
	private readonly List<Ring> _rings = new();
	private readonly List<Float> _floats = new();
	private readonly List<Fail> _fails = new();

	private float _shake;
	private double _now; // 自己累计的时间（低电量脉冲用，比 Godot 的 tick 好测）

	private int _anchor = -1; // 拖拽起点格子
	private int _cur = -1;    // 拖拽终点格子
	private bool _dragging;

	// ===================== 子节点 =====================

	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _board = null!;
	private Node2D _fuse = null!;
	private FuseView _fuseView = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _restartButton = null!;
	private Button _timeButton = null!;

	private GridView _grid = null!;
	private Font? _font;

	private Label _iqLabel = null!;
	private Label _hintLabel = null!;

	private Control _setup = null!;
	private readonly Button[] _timeButtons = new Button[Times.Length];
	private readonly Label[] _timeSubs = new Label[Times.Length];
	private readonly Label[] _timeBests = new Label[Times.Length];

	private ColorRect _result = null!;
	private Label _resultTitle = null!;
	private Label _resultInfo = null!;
	private Label _resultNew = null!;
	private Button _againButton = null!;

	// HUD 文本缓存：只在数值真的变了才重建字符串
	private int _hudScore = int.MinValue;

	// 画棋盘用的样式盒（缓存起来，_Draw 里不能每格 new 一个）
	private StyleBoxFlat _sbFrame = null!;
	private StyleBoxFlat _sbWood = null!;
	private StyleBoxFlat _sbFelt = null!;
	private StyleBoxFlat _sbHole = null!;
	private StyleBoxFlat _sbTileEdge = null!;
	private StyleBoxFlat _sbTile = null!;
	private StyleBoxFlat _sbPopTile = null!;
	private StyleBoxFlat _sbSelCell = null!;
	private StyleBoxFlat _sbSelEdge = null!;
	private StyleBoxFlat _sbBubble = null!;
	private StyleBoxFlat _sbFail = null!;
	private StyleBoxFlat _sbFuseSlot = null!;
	private StyleBoxFlat _sbFuseFill = null!;

	private float _tile = 46f;
	private Vector2 _origin;

	private float StageW => Size.X > 0f ? Size.X : DesignW;

	// ===================== 生命周期 =====================

	public override void _Ready()
	{
		_stage = GetNode<Control>("Stage");
		_background = GetNode<TextureRect>("Stage/Background");
		_board = GetNode<Node2D>("Stage/Board");
		_fuse = GetNode<Node2D>("Stage/Fuse");
		_ui = GetNode<Control>("UI");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_restartButton = GetNode<Button>("UI/TopBar/RestartButton");
		_timeButton = GetNode<Button>("UI/TopBar/TimeButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		// 根节点铺满全屏，默认的 MouseFilter = Stop 会「认领」所有落在空白处的点击，
		// 把事件从引擎的 GUI 流程里截走。棋盘画在 Node2D 上、靠代码自己接事件，
		// 被截走就等于拖不动 —— 所以根节点必须让路。
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = GameArt.MakeUiTheme();
		_font = GameArt.UiFont; // _Draw 里画数字要用，Theme 里那份拿不到

		// 背景：暖色木屋渐变，和托儿所这个题材搭一点
		_background.Texture = GameArt.VerticalGradient(new Color("#123a2a"), new Color("#2a1f45"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		BuildStyleBoxes();
		BuildTopBar();
		BuildHud();
		BuildSetup();
		BuildResult();
		LoadBest();

		// 棋盘和引线各自维护一块画布。注意 QueueRedraw 必须打在**真正重写了 _Draw 的那个节点**上 ——
		// 打在父节点 Fuse / Board 上是没用的，子画布不会跟着重画（引线曾经因此整根消失）。
		_grid = new GridView(this);
		_board.AddChild(_grid);
		_fuseView = new FuseView(this);
		_fuse.AddChild(_fuseView);

		Layout();
		GetViewport().SizeChanged += Layout;

		GD.Print($"[Nursery] ready. best={DescribeBest()} selftest={SelftestFlag.Describe()}");

		if (SelftestFlag.Read() == SelftestFlag.TokenNursery)
			_ = RunSelfTestAsync();
		else
			ShowSetup();
	}

	private void Layout()
	{
		// Control 不会自动铺满父节点：尺寸是 0 的话里面的东西全塌缩成一列看不见的
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」：
		// TextureRect 的 min size 跟着纹理走，而渐变纹理只有 8×256，
		// 不铺满就只在左上角画一小块，其余全是视口清屏色（0.3 灰）。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		ComputeGrid();
	}

	/// <summary>
	/// 按行列数算格子边长和棋盘原点：边长取「宽高两个方向都放得下」的较小值，
	/// 这样棋盘永远是整体居中的，不需要手写坐标。
	/// </summary>
	private void ComputeGrid()
	{
		float availW = StageW - PadX * 2f;
		float availH = BoardBottom - BoardTop;
		float tile = Mathf.Min(availW / Cols, availH / Rows);
		_tile = Mathf.Clamp(Mathf.Floor(tile), MinTile, MaxTile);

		_origin = new Vector2(
			(StageW - _tile * Cols) * 0.5f,
			BoardTop + (availH - _tile * Rows) * 0.5f);
		_grid.Position = _origin;
	}

	private void BuildStyleBoxes()
	{
		// 木框 + 绿绒面：截图里就是这块木质托板
		_sbFrame = GameArt.MakeBox(new Color("#5d3a18"), 24, new Color("#3a2310"));
		_sbWood = GameArt.MakeBox(new Color("#9c6a2c"), 18);
		_sbFelt = GameArt.MakeBox(new Color("#1f7a4d"), 12, new Color("#12523a"));
		// 已消除的格子留一个「洞」：比绒面暗一点，一眼能看出哪里被消掉了
		_sbHole = GameArt.MakeBox(new Color(0f, 0f, 0f, 0.20f), 8);

		_sbTileEdge = GameArt.MakeBox(new Color("#cfc7ad"), 8);
		_sbTile = GameArt.MakeBox(new Color("#fdfcf3"), 8);
		_sbPopTile = GameArt.MakeBox(new Color("#fdfcf3"), 8);

		_sbSelCell = GameArt.MakeBox(new Color(1f, 1f, 1f, 0.20f), 8);
		_sbSelEdge = GameArt.MakeBox(new Color(0f, 0f, 0f, 0f), 10, new Color(1f, 1f, 1f, 0.85f));
		_sbBubble = GameArt.MakeBox(new Color(0.06f, 0.08f, 0.12f, 0.92f), 14, new Color(1f, 1f, 1f, 0.35f));
		_sbFail = GameArt.MakeBox(new Color(1f, 0.30f, 0.37f, 0.30f), 8);

		_sbFuseSlot = GameArt.MakeBox(new Color(0.05f, 0.05f, 0.07f, 0.70f), 11, new Color(1f, 1f, 1f, 0.16f));
		_sbFuseFill = GameArt.MakeBox(new Color("#ffb347"), 11);
	}

	// ===================== 一局的开始与结束 =====================

	private void StartGame(int timeIndex)
	{
		var def = Times[Mathf.Clamp(timeIndex, 0, Times.Length - 1)];
		_timeIndex = Mathf.Clamp(timeIndex, 0, Times.Length - 1);
		_totalTime = def.Seconds;
		_remain = def.Seconds;
		_score = 0;
		_cleared = 0;

		// 注意别写 Environment.TickCount：这个文件里有 using Godot，
		// 那个名字会解析到 Godot.Environment（3D 环境资源）上去，编译直接报错。
		_rng = new System.Random(_seed != 0 ? _seed : (int)(Time.GetTicksMsec() & 0x7FFFFFFF));

		_pops.Clear();
		_rings.Clear();
		_floats.Clear();
		_fails.Clear();
		_shake = 0f;
		ClearSelection();
		_board.Position = Vector2.Zero;

		GenerateBoard();

		_phase = Phase.Play;
		_setup.Visible = false;
		_result.Visible = false;

		_hudScore = int.MinValue;
		UpdateHud();
		Redraw();

		GD.Print($"[Nursery] start \"{def.Label}\" {Rows}×{Cols} · 可解块数 {_solution.Count}");
	}

	// ===================== 棋盘生成 =====================
	//
	// 目标：既要随机、又要保证一定能消完。
	// 做法是把棋盘切成一块块「面积 2~3」的小矩形，每块内部填一组和为 10 的数字。
	// 这样每块自己就是一步合法操作，按块消完 → 整盘清空，可解性由构造保证。
	//
	// 块为什么必须这么小：一块 k 格要凑出和 = 10，平均值就只有 10/k。
	// 一开始放成 2~10 格的时候，9 格的块里只能是「八个 1 + 一个 2」，
	// 整盘就成了一片 1、2、3，玩起来又难看又难消。切成 2~3 格之后，
	// 两格对子是 1+9 / 2+8 …（每个数字出现的机会均等），三格才带一点小数字，
	// 盘面才和原版一样是散布开的 1~9。

	private void GenerateBoard()
	{
		_cells = new int[Rows * Cols];
		_solution.Clear();

		Partition(new Region(0, 0, Cols, Rows), _solution);
		foreach (var reg in _solution)
			FillRegion(reg);
	}

	/// <summary>把一块矩形递归切成若干「面积 ≤ <see cref="MaxLeaf"/> 且 ≥ 2」的小矩形。</summary>
	private void Partition(Region reg, List<Region> leaves)
	{
		if (reg.Area <= MaxLeaf)
		{
			leaves.Add(reg);
			return;
		}

		// 切线必须让两边面积都 ≥ 2：面积 1 的块凑不出 10（单个数字最大才 9），
		// 一旦切出这种块，整盘就必然有解不了的地方。
		var hSplits = new List<int>();
		for (int k = 1; k < reg.H; k++)
			if (k * reg.W >= 2 && (reg.H - k) * reg.W >= 2)
				hSplits.Add(k);

		var vSplits = new List<int>();
		for (int k = 1; k < reg.W; k++)
			if (k * reg.H >= 2 && (reg.W - k) * reg.H >= 2)
				vSplits.Add(k);

		bool horizontal = hSplits.Count > 0 && (vSplits.Count == 0 || _rng.Next(2) == 0);
		if (horizontal)
		{
			int k = hSplits[_rng.Next(hSplits.Count)];
			Partition(new Region(reg.C, reg.R, reg.W, k), leaves);
			Partition(new Region(reg.C, reg.R + k, reg.W, reg.H - k), leaves);
		}
		else if (vSplits.Count > 0)
		{
			int k = vSplits[_rng.Next(vSplits.Count)];
			Partition(new Region(reg.C, reg.R, k, reg.H), leaves);
			Partition(new Region(reg.C + k, reg.R, reg.W - k, reg.H), leaves);
		}
		else
		{
			// 理论上到不了这里：面积 > 10 的矩形总能找到一条两边都 ≥ 2 格的切线。
			GD.PushError($"[Nursery] 无法切分矩形 {reg.W}×{reg.H}");
			leaves.Add(reg);
		}
	}

	/// <summary>
	/// 往一块矩形里填一组和为 10 的数字（每格 1~9）。
	///
	/// 关键是**均匀**：每一格的值都按「剩下的格子还能怎么分」等概率地抽（见 <see cref="PickPart"/>）。
	/// 换成「先全填 1、再把差额一格一格随机加上去」的话，中间值会被抽爆（加法的路径数在中间最大），
	/// 而且块一大就必然整片都是 1。
	/// </summary>
	private void FillRegion(Region reg)
	{
		int area = reg.Area;
		var vals = new int[area];
		int left = Target;
		for (int i = 0; i < area; i++)
		{
			int v = PickPart(left, area - i - 1);
			vals[i] = v;
			left -= v;
		}

		// 位置打乱：同一块里的数字别按行列顺序躺好，否则盘面一眼就能看出切块规律
		var pos = new List<int>(area);
		for (int dr = 0; dr < reg.H; dr++)
			for (int dc = 0; dc < reg.W; dc++)
				pos.Add(Idx(reg.C + dc, reg.R + dr));
		for (int i = pos.Count - 1; i > 0; i--)
		{
			int j = _rng.Next(i + 1);
			(pos[i], pos[j]) = (pos[j], pos[i]);
		}

		for (int i = 0; i < area; i++)
			_cells[pos[i]] = vals[i];
	}

	/// <summary>
	/// 「还剩 <paramref name="left"/> 要分给后面 <paramref name="rest"/> 格」时，等概率抽这一格的值。
	/// 每种候选值的权重 = 它留给后面那几格的分配方案数，所以逐格抽下来就是整体均匀。
	/// </summary>
	private int PickPart(int left, int rest)
	{
		int lo = Mathf.Max(1, left - 9 * rest); // 不能让后面的格子超过 9
		int hi = Mathf.Min(9, left - rest);     // 后面每格至少留 1

		var weights = new int[hi - lo + 1];
		int total = 0;
		for (int v = lo; v <= hi; v++)
		{
			int w = Ways(left - v, rest);
			weights[v - lo] = w;
			total += w;
		}

		int pick = _rng.Next(total);
		for (int v = lo; v <= hi; v++)
		{
			pick -= weights[v - lo];
			if (pick < 0)
				return v;
		}
		return hi; // 走不到：total ≥ 1 时上面一定会返回
	}

	/// <summary>把 <paramref name="sum"/> 分给 <paramref name="count"/> 格、每格 1~9 的方案数。</summary>
	private static int Ways(int sum, int count)
	{
		if (count == 0)
			return sum == 0 ? 1 : 0;
		int lo = Mathf.Max(1, sum - 9 * (count - 1));
		int hi = Mathf.Min(9, sum - (count - 1));
		int total = 0;
		for (int v = lo; v <= hi; v++)
			total += Ways(sum - v, count - 1);
		return total;
	}

	// ===================== 主循环 =====================

	/// <summary>
	/// 推进倒计时 + 动画。棋盘静止时不会 QueueRedraw；引线每帧重画一次（就一根条，很便宜）。
	/// </summary>
	public override void _Process(double delta)
	{
		_now += delta;
		bool anim = false;

		if (_phase == Phase.Play)
		{
			_remain -= (float)delta;
			_fuseView.QueueRedraw();
			if (_remain <= 0f)
			{
				_remain = 0f;
				GameOver(false);
			}
		}

		for (int i = _pops.Count - 1; i >= 0; i--)
		{
			_pops[i].Age += (float)delta;
			anim = true;
			if (_pops[i].Age >= PopLife)
				_pops.RemoveAt(i);
		}

		for (int i = _rings.Count - 1; i >= 0; i--)
		{
			_rings[i].Age += (float)delta;
			anim = true;
			if (_rings[i].Age >= 0.55f)
				_rings.RemoveAt(i);
		}

		for (int i = _floats.Count - 1; i >= 0; i--)
		{
			_floats[i].Age += (float)delta;
			anim = true;
			if (_floats[i].Age >= FloatLife)
				_floats.RemoveAt(i);
		}

		for (int i = _fails.Count - 1; i >= 0; i--)
		{
			_fails[i].Age += (float)delta;
			anim = true;
			if (_fails[i].Age >= FailLife)
				_fails.RemoveAt(i);
		}

		if (_shake > 0f)
		{
			_shake = Mathf.Max(0f, _shake - ShakeDecay * (float)delta);
			_board.Position = new Vector2(
				(float)(_rng.NextDouble() * 2.0 - 1.0) * _shake,
				(float)(_rng.NextDouble() * 2.0 - 1.0) * _shake);
			anim = true;
			if (_shake <= 0f)
				_board.Position = Vector2.Zero;
		}

		if (anim)
			Redraw();
	}

	// ===================== 操作 =====================

	/// <summary>
	/// 结算当前框选。从输入层剥出来是为了两件事：自测可以直接调它，规则也只有一份。
	/// 返回是否真的消掉了。
	/// </summary>
	private bool ResolveSelection()
	{
		if (_phase != Phase.Play || _anchor < 0 || _cur < 0)
		{
			ClearSelection();
			return false;
		}

		SelectionBounds(out int c0, out int r0, out int c1, out int r1);
		int sum = 0, count = 0;
		for (int r = r0; r <= r1; r++)
			for (int c = c0; c <= c1; c++)
			{
				int v = _cells[Idx(c, r)];
				if (v > 0)
				{
					sum += v;
					count++;
				}
			}

		ClearSelection();

		if (sum == Target)
		{
			Eliminate(c0, r0, c1, r1, count);
			return true;
		}

		// 凑不成：红闪一下 + 轻轻震屏。不扣分、不扣时间，只是给个「这么框不行」的反馈。
		_fails.Add(new Fail { C0 = c0, R0 = r0, C1 = c1, R1 = r1, Age = 0f });
		_shake = 6f;
		GD.Print($"[Nursery] miss: 框住 {count} 个数字 · 和={sum}（要 {Target}）");
		Redraw();
		return false;
	}

	private void Eliminate(int c0, int r0, int c1, int r1, int count)
	{
		for (int r = r0; r <= r1; r++)
			for (int c = c0; c <= c1; c++)
			{
				int i = Idx(c, r);
				if (_cells[i] <= 0)
					continue;
				_pops.Add(new Pop { Index = i, Value = _cells[i], Age = 0f });
				_cells[i] = 0;
			}

		_score += count;
		_cleared += count;

		var center = new Vector2(
			(c0 + (c1 - c0 + 1) * 0.5f) * _tile,
			(r0 + (r1 - r0 + 1) * 0.5f) * _tile);
		_floats.Add(new Float { Pos = center, Text = $"+{count}", Tint = new Color("#ffe08a"), Age = 0f });
		_rings.Add(new Ring { Pos = center, Age = 0f, MaxR = Mathf.Max(_tile * 1.2f, (c1 - c0 + 1) * _tile * 0.8f) });

		GD.Print($"[Nursery] clear {count} 格 · 智商={_score} 已消 {_cleared}/{Rows * Cols}");
		UpdateHud();
		Redraw();

		if (_cleared >= Rows * Cols)
			GameOver(true);
	}

	/// <summary>时间到（<paramref name="allCleared"/> = false）或提前清空整盘。</summary>
	private void GameOver(bool allCleared)
	{
		if (_phase == Phase.Over)
			return;

		_phase = Phase.Over;
		ClearSelection();
		_remain = Mathf.Max(0f, _remain);

		bool record = _score > _best[_timeIndex];
		if (record)
		{
			_best[_timeIndex] = _score;
			SaveBest();
		}

		ShowResult(
			allCleared ? "全部消除！" : "时间到！",
			$"本局智商 {_score} · 消除 {_cleared}/{Rows * Cols} 个数字",
			record && _score > 0 ? "★ 新纪录！" : "");

		UpdateHud();
		Redraw();

		GD.Print($"[Nursery] over. time={Times[_timeIndex].Tag} score={_score} " +
				 $"cleared={_cleared} record={record} allCleared={allCleared}");
	}

	// ===================== 输入 =====================
	//
	// 这里必须是 _Input，不能是 _UnhandledInput：棋盘画在 Node2D 上、不是 Control，
	// 而本场景的根节点是个铺满全屏的 Control，会把空白处的点击先「认领」掉。
	// 雷霆战机 / 羊了个羊 / 扫雷用的都是 _Input，这里保持一致。
	//
	// 同样要过滤「模拟事件」（device = -1）：项目开了 mouse→touch，而 touch→mouse 又是
	// 引擎默认开的，点一下鼠标会同时收到真鼠标事件和模拟触摸事件，两个都处理就会
	// 一次拖拽被结算两遍。

	private static bool IsEmulated(InputEvent e) => e.Device == -1;

	public override void _Input(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventMouseButton mb when !IsEmulated(mb) && mb.ButtonIndex == MouseButton.Left:
				if (mb.Pressed)
					BeginPress(mb.Position);
				else
					EndPress(mb.Position);
				break;

			case InputEventScreenTouch st when !IsEmulated(st):
				if (st.Pressed)
					BeginPress(st.Position);
				else
					EndPress(st.Position);
				break;

			case InputEventMouseMotion mm when !IsEmulated(mm) && _dragging:
				DragTo(mm.Position);
				break;

			case InputEventScreenDrag sd when !IsEmulated(sd) && _dragging:
				DragTo(sd.Position);
				break;
		}
	}

	private void BeginPress(Vector2 pos)
	{
		if (_phase != Phase.Play)
			return;
		int idx = CellAt(pos);
		if (idx < 0)
			return;
		_anchor = idx;
		_cur = idx;
		_dragging = true;
		Redraw();
	}

	private void DragTo(Vector2 pos)
	{
		if (!_dragging)
			return;
		// 拖出棋盘外也不放开：夹到边上，手感是「一直拉到边」
		int idx = CellAtClamped(pos);
		if (idx == _cur)
			return;
		_cur = idx;
		Redraw();
	}

	private void EndPress(Vector2 pos)
	{
		if (!_dragging)
			return;
		int idx = CellAt(pos);
		if (idx >= 0)
			_cur = idx; // 抬手落在棋盘外时沿用拖拽中的最后一格
		_dragging = false;
		ResolveSelection();
	}

	private void ClearSelection()
	{
		_anchor = -1;
		_cur = -1;
		_dragging = false;
	}

	private void SelectionBounds(out int c0, out int r0, out int c1, out int r1)
	{
		int ac = ColOf(_anchor), ar = RowOf(_anchor);
		int cc = ColOf(_cur), cr = RowOf(_cur);
		c0 = Mathf.Min(ac, cc);
		c1 = Mathf.Max(ac, cc);
		r0 = Mathf.Min(ar, cr);
		r1 = Mathf.Max(ar, cr);
	}

	/// <summary>屏幕坐标 → 格子下标；不在棋盘上返回 -1。</summary>
	private int CellAt(Vector2 pos)
	{
		if (_cells.Length == 0)
			return -1;
		Vector2 local = pos - _grid.GlobalPosition;
		if (local.X < 0f || local.Y < 0f)
			return -1;
		int c = (int)(local.X / _tile);
		int r = (int)(local.Y / _tile);
		if (c >= Cols || r >= Rows)
			return -1;
		return Idx(c, r);
	}

	/// <summary>同上，但拖到棋盘外时夹到最近的格子（拖拽中专用）。</summary>
	private int CellAtClamped(Vector2 pos)
	{
		Vector2 local = pos - _grid.GlobalPosition;
		int c = Mathf.Clamp((int)Mathf.Floor(local.X / _tile), 0, Cols - 1);
		int r = Mathf.Clamp((int)Mathf.Floor(local.Y / _tile), 0, Rows - 1);
		return Idx(c, r);
	}

	private static int Idx(int c, int r) => r * Cols + c;
	private static int ColOf(int i) => i % Cols;
	private static int RowOf(int i) => i / Cols;

	private void Redraw()
	{
		_grid.QueueRedraw();
		_fuseView.QueueRedraw();
	}

	// ===================== 棋盘绘制 =====================

	private Rect2 CellRect(int c, int r)
		=> new(c * _tile + 2f, r * _tile + 2f, _tile - 4f, _tile - 4f);

	/// <summary>棋盘节点的全部内容。逻辑都在外面，它只负责画。</summary>
	private void DrawBoard(CanvasItem ci)
	{
		if (_cells.Length == 0)
		{
			// 还没选时长：棋盘位给一句提示，别让屏幕中间空着一大块
			if (_font != null)
				ci.DrawString(_font, new Vector2(0f, (BoardTop + BoardBottom) * 0.5f), "选个时长开始吧",
					HorizontalAlignment.Center, StageW, 40, new Color(1f, 1f, 1f, 0.40f));
			return;
		}

		float w = _tile * Cols, h = _tile * Rows;

		// 木质托板：外框（深）→ 木面（亮）→ 绿绒（棋盘本体）
		ci.DrawStyleBox(_sbFrame, new Rect2(-18f, -18f, w + 36f, h + 36f));
		ci.DrawStyleBox(_sbWood, new Rect2(-10f, -10f, w + 20f, h + 20f));
		ci.DrawStyleBox(_sbFelt, new Rect2(0f, 0f, w, h));

		int digitSize = Mathf.Max(12, (int)(_tile * 0.56f));
		var digitColor = new Color("#2b3653");

		for (int r = 0; r < Rows; r++)
		{
			for (int c = 0; c < Cols; c++)
			{
				int i = Idx(c, r);
				int v = _cells[i];
				var rect = CellRect(c, r);

				if (v <= 0)
				{
					ci.DrawStyleBox(_sbHole, rect); // 空洞
					continue;
				}

				// 底下压一层浅色边、上面盖白面，叠出「一块瓷砖」的厚度
				ci.DrawStyleBox(_sbTileEdge, new Rect2(rect.Position + new Vector2(0f, 2.5f), rect.Size));
				ci.DrawStyleBox(_sbTile, rect);
				DrawText(ci, rect, v.ToString(), digitColor, digitSize);
			}
		}

		if (_dragging && _anchor >= 0 && _cur >= 0)
			DrawSelection(ci);

		// 凑不成 10 的红闪
		foreach (var f in _fails)
		{
			float t = Mathf.Clamp(f.Age / FailLife, 0f, 1f);
			_sbFail.BgColor = new Color(1f, 0.30f, 0.37f, 0.34f * (1f - t));
			for (int r = f.R0; r <= f.R1; r++)
				for (int c = f.C0; c <= f.C1; c++)
					ci.DrawStyleBox(_sbFail, CellRect(c, r));
		}

		// 消除时「缩掉」的瓷砖（数据已经清空，靠这份快照把它画出来）
		foreach (var p in _pops)
		{
			float t = Mathf.Clamp(p.Age / PopLife, 0f, 1f);
			int c = ColOf(p.Index), r = RowOf(p.Index);
			var rect = CellRect(c, r);
			float sc = 1f - 0.42f * t;
			ci.DrawSetTransform(rect.GetCenter(), 0f, Vector2.One * sc);
			_sbPopTile.BgColor = new Color(1f, 1f, 0.95f, 1f - t);
			ci.DrawStyleBox(_sbPopTile, new Rect2(-rect.Size * 0.5f, rect.Size));
			DrawText(ci, new Rect2(-rect.Size * 0.5f, rect.Size), p.Value.ToString(),
				new Color(0.17f, 0.21f, 0.33f, 1f - t), digitSize);
			ci.DrawSetTransform(Vector2.Zero);
		}

		// 消除时的金环
		foreach (var ring in _rings)
		{
			float t = Mathf.Clamp(ring.Age / 0.55f, 0f, 1f);
			float rad = ring.MaxR * (0.30f + 0.70f * t);
			float alpha = 1f - t;
			ci.DrawCircle(ring.Pos, rad, new Color(ring.Tint.R, ring.Tint.G, ring.Tint.B, 0.16f * alpha));
			ci.DrawArc(ring.Pos, rad, 0f, Mathf.Tau, 32, new Color(ring.Tint.R, ring.Tint.G, ring.Tint.B, 0.9f * alpha), 4f, true);
		}

		// 「+N」往上飘
		foreach (var fl in _floats)
		{
			float t = Mathf.Clamp(fl.Age / FloatLife, 0f, 1f);
			var color = new Color(fl.Tint.R, fl.Tint.G, fl.Tint.B, 1f - t * t);
			var box = new Rect2(fl.Pos.X - 90f, fl.Pos.Y - 30f - 52f * t, 180f, 56f);
			DrawText(ci, box, fl.Text, color, 42);
		}
	}

	/// <summary>拖拽中的方框：格子高亮 + 外框 + 中间飘着的「和」。</summary>
	private void DrawSelection(CanvasItem ci)
	{
		SelectionBounds(out int c0, out int r0, out int c1, out int r1);

		int sum = 0;
		for (int r = r0; r <= r1; r++)
			for (int c = c0; c <= c1; c++)
				sum += _cells[Idx(c, r)];

		bool good = sum == Target;

		// 和等于 10 就整个框变金 —— 抬手之前就知道这一步成不成
		_sbSelCell.BgColor = good ? new Color(1f, 0.83f, 0.25f, 0.38f) : new Color(1f, 1f, 1f, 0.20f);
		for (int r = r0; r <= r1; r++)
			for (int c = c0; c <= c1; c++)
				ci.DrawStyleBox(_sbSelCell, CellRect(c, r));

		var box = new Rect2(
			c0 * _tile + 3f, r0 * _tile + 3f,
			(c1 - c0 + 1) * _tile - 6f, (r1 - r0 + 1) * _tile - 6f);
		_sbSelEdge.BorderColor = good ? new Color("#ffd23f") : new Color(1f, 1f, 1f, 0.85f);
		ci.DrawStyleBox(_sbSelEdge, box);

		DrawSumBubble(ci, box, sum, good);
	}

	private void DrawSumBubble(CanvasItem ci, Rect2 box, int sum, bool good)
	{
		var size = new Vector2(96f, 48f);
		float x = Mathf.Clamp(box.GetCenter().X - size.X * 0.5f, 2f, _tile * Cols - size.X - 2f);
		// 默认飘在方框上方，顶到棋盘上沿就翻到下面去
		float y = box.Position.Y - size.Y - 8f;
		if (y < 2f)
			y = box.Position.Y + box.Size.Y + 8f;

		_sbBubble.BgColor = good ? new Color(0.10f, 0.35f, 0.20f, 0.94f) : new Color(0.06f, 0.08f, 0.12f, 0.90f);
		var bubble = new Rect2(x, y, size);
		ci.DrawStyleBox(_sbBubble, bubble);
		DrawText(ci, bubble, $"= {sum}", good ? new Color("#ffd23f") : Colors.White, 28);
	}

	/// <summary>在格子里居中画一行字（垂直居中要自己按 ascent/descent 算基线）。</summary>
	private void DrawText(CanvasItem ci, Rect2 box, string text, Color color, int size)
	{
		if (_font == null)
			return;
		size = Mathf.Max(10, size);
		float ascent = _font.GetAscent(size);
		float descent = _font.GetDescent(size);
		float baseline = box.GetCenter().Y + (ascent - descent) * 0.5f;
		ci.DrawString(_font, new Vector2(box.Position.X, baseline), text,
			HorizontalAlignment.Center, box.Size.X, size, color);
	}

	// ===================== 引线 + 倒计时 =====================

	/// <summary>
	/// 燃烧的引线：左边一颗炸弹，引线从右往左烧，烧到炸弹就「砰」。
	/// 剩余时间 = 还没烧掉的那段长度，比一根进度条更符合「限时」的直觉。
	/// </summary>
	private void DrawFuse(CanvasItem ci)
	{
		if (_phase == Phase.Setup || _totalTime <= 0f)
			return;

		bool low = _phase == Phase.Play && _remain <= LowTime;
		float f = Mathf.Clamp(_remain / _totalTime, 0f, 1f);

		// 槽：整根引线的位置（烧掉的部分留空）
		ci.DrawStyleBox(_sbFuseSlot, new Rect2(FuseLeft, FuseY - FuseH * 0.5f, FuseRight - FuseLeft, FuseH));

		float burnX = FuseLeft + (1f - f) * (FuseRight - FuseLeft);
		if (burnX < FuseRight - 2f)
		{
			_sbFuseFill.BgColor = low
				? new Color("#ff6a5c")
				: new Color("#ffb347");
			ci.DrawStyleBox(_sbFuseFill, new Rect2(burnX, FuseY - FuseH * 0.5f, FuseRight - burnX, FuseH));

			// 火星：烧到哪儿就闪在哪儿
			float pulse = 0.7f + 0.3f * Mathf.Sin((float)_now * 14f);
			ci.DrawCircle(new Vector2(burnX, FuseY), FuseH * 0.75f * pulse, new Color(1f, 0.78f, 0.30f, 0.55f));
			ci.DrawCircle(new Vector2(burnX, FuseY), FuseH * 0.38f, new Color(1f, 0.96f, 0.72f));
		}

		// 炸弹：快没时间时抖一抖
		var bomb = new Vector2(BombX, FuseY);
		if (low)
			bomb += new Vector2(Mathf.Sin((float)_now * 30f) * 2.4f, Mathf.Cos((float)_now * 26f) * 2.4f);
		GameArt.DrawMine(ci, BombR, bomb);

		// 倒计时数字
		string text = FormatTime(_remain);
		var box = new Rect2(FuseTextLeft, FuseY - 20f, StageW - FuseTextLeft - 8f, 40f);
		DrawText(ci, box, text, low ? new Color("#ff9a8f") : new Color(1f, 1f, 1f, 0.88f), 30);
	}

	private static string FormatTime(float seconds)
	{
		int s = Mathf.Max(0, Mathf.CeilToInt(seconds));
		return $"{s / 60}:{s % 60:00}";
	}

	// ===================== HUD / 面板 =====================

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

		GameArt.StyleButton(_restartButton, new Color("#ff8f6b"), Colors.White, fontSize: 32);
		_restartButton.CustomMinimumSize = new Vector2(140, 80);
		_restartButton.Text = "重开";
		_restartButton.Pressed += () =>
		{
			if (_totalTime > 0f)
				StartGame(_timeIndex);
			else
				ShowSetup();
		};

		GameArt.StyleButton(_timeButton, new Color("#4fa8ff"), Colors.White, fontSize: 32);
		_timeButton.CustomMinimumSize = new Vector2(140, 80);
		_timeButton.Text = "换时长";
		_timeButton.Pressed += ShowSetup;
	}

	private void BuildHud()
	{
		_iqLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		GameArt.OutlineText(_iqLabel, new Color("#ffd77a"), 40, 8);
		_iqLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_iqLabel.GrowHorizontal = GrowDirection.Both;
		_iqLabel.OffsetTop = 108;
		_iqLabel.OffsetBottom = 166;
		_ui.AddChild(_iqLabel);

		// 规则一句话说清，而且全程不变 —— 面板和棋盘用的是同一句话。
		_hintLabel = new Label
		{
			Text = $"拖出一个方框 · 框里的数字相加等于 {Target} 就消除",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_hintLabel.AddThemeFontSizeOverride("font_size", 24);
		_hintLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.55f));
		_hintLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_hintLabel.GrowHorizontal = GrowDirection.Both;
		_hintLabel.GrowVertical = GrowDirection.Begin;
		_hintLabel.OffsetTop = -108;
		_hintLabel.OffsetBottom = -58;
		_ui.AddChild(_hintLabel);

		UpdateHud();
	}

	private void UpdateHud()
	{
		if (_score != _hudScore)
		{
			_hudScore = _score;
			_iqLabel.Text = $"当前智商 {_score}";
		}
	}

	/// <summary>
	/// 时长面板。三个大按钮而不是小 chip：这是进游戏前的第一个决定，
	/// 得让人一眼看清「限时多久、这档纪录多少」。
	/// </summary>
	private void BuildSetup()
	{
		_setup = new Control { MouseFilter = MouseFilterEnum.Ignore };
		_setup.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_setup.Visible = false;
		_ui.AddChild(_setup);

		var dim = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.66f),
			MouseFilter = MouseFilterEnum.Stop, // 挡住底下的棋盘，选时长时不能继续动手
		};
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_setup.AddChild(dim);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.08f, 0.10f, 0.19f, 0.98f), 34, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 36;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		dim.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 16);
		panel.AddChild(col);

		var title = new Label { Text = "开局托儿所", HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(title, Colors.White, 60, 8);
		col.AddChild(title);

		var sub = new Label
		{
			Text = $"框住数字 · 相加等于 {Target} 就消除",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		sub.AddThemeFontSizeOverride("font_size", 23);
		sub.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.66f));
		col.AddChild(sub);

		for (int i = 0; i < Times.Length; i++)
		{
			int idx = i;
			var b = new Button { CustomMinimumSize = new Vector2(560, 112) };
			b.Text = ""; // 内容全部由子节点排，按钮自己不画文字
			GameArt.StyleButton(b, new Color(1f, 1f, 1f, 0.09f), Colors.White, radius: 24,
				border: new Color(1f, 1f, 1f, 0.26f));
			b.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.18f), 24, new Color(1f, 1f, 1f, 0.5f)));
			b.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.06f), 24, new Color(1f, 1f, 1f, 0.4f)));
			b.AddThemeStyleboxOverride("focus", GameArt.MakeBox(new Color(0f, 0f, 0f, 0f), 24));
			b.Pressed += () =>
			{
				GD.Print($"[Nursery] time -> {Times[idx].Label}");
				StartGame(idx);
			};
			col.AddChild(b);
			_timeButtons[i] = b;

			// 按钮里的子节点必须 MouseFilter = Ignore，否则它们会把点击吃掉，按钮收不到 pressed
			var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
			row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			row.OffsetLeft = 28;
			row.OffsetRight = -28;
			row.OffsetTop = 12;
			row.OffsetBottom = -12;
			row.AddThemeConstantOverride("separation", 16);
			b.AddChild(row);

			var cellCol = new VBoxContainer
			{
				MouseFilter = MouseFilterEnum.Ignore,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
			};
			cellCol.AddThemeConstantOverride("separation", 2);
			row.AddChild(cellCol);

			var name = new Label { Text = Times[i].Label, MouseFilter = MouseFilterEnum.Ignore };
			GameArt.OutlineText(name, Times[i].Tint, 40, 6);
			cellCol.AddChild(name);

			var info = new Label { MouseFilter = MouseFilterEnum.Ignore };
			info.AddThemeFontSizeOverride("font_size", 22);
			info.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.72f));
			cellCol.AddChild(info);
			_timeSubs[i] = info;

			var best = new Label
			{
				MouseFilter = MouseFilterEnum.Ignore,
				VerticalAlignment = VerticalAlignment.Center,
			};
			best.AddThemeFontSizeOverride("font_size", 26);
			best.AddThemeColorOverride("font_color", new Color("#ffd77a"));
			row.AddChild(best);
			_timeBests[i] = best;
		}

		var quit = new Button { Text = "返回首页", CustomMinimumSize = new Vector2(560, 88) };
		GameArt.StyleButton(quit, new Color("#8f7bff"), Colors.White, fontSize: 32);
		quit.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		col.AddChild(quit);
	}

	private void ShowSetup()
	{
		_phase = Phase.Setup;
		ClearSelection();
		_result.Visible = false;
		RefreshSetup();
		_setup.Visible = true;
		UpdateHud();
		Redraw();
	}

	private void RefreshSetup()
	{
		for (int i = 0; i < Times.Length; i++)
		{
			_timeSubs[i].Text = $"限时 {Times[i].Seconds} 秒 · 消一个数字得 1 点智商";
			_timeBests[i].Text = _best[i] > 0 ? $"最高\n{_best[i]} 分" : "最高\n—";
		}
	}

	private void BuildResult()
	{
		_result = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.62f),
			MouseFilter = MouseFilterEnum.Stop,
			Visible = false,
		};
		_result.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_result);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.08f, 0.10f, 0.19f, 0.98f), 32, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 40;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		_result.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 16);
		panel.AddChild(col);

		_resultTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(_resultTitle, Colors.White, 54, 8);
		col.AddChild(_resultTitle);

		_resultInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_resultInfo.AddThemeFontSizeOverride("font_size", 30);
		_resultInfo.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.86f));
		col.AddChild(_resultInfo);

		_resultNew = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_resultNew.AddThemeFontSizeOverride("font_size", 30);
		_resultNew.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(_resultNew);

		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 16);
		col.AddChild(row);

		_againButton = MakeOverlayButton("再来一局", new Color("#ff8f6b"));
		_againButton.Pressed += () => StartGame(_timeIndex);
		row.AddChild(_againButton);

		var change = MakeOverlayButton("换时长", new Color("#4fa8ff"));
		change.Pressed += ShowSetup;
		row.AddChild(change);

		var quit = MakeOverlayButton("回首页", new Color("#8f7bff"));
		quit.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		row.AddChild(quit);
	}

	private void ShowResult(string title, string info, string extra)
	{
		_resultTitle.Text = title;
		_resultInfo.Text = info;
		_resultNew.Text = extra;
		_resultNew.Visible = extra.Length > 0;
		_result.Visible = true;
	}

	private Button MakeOverlayButton(string text, Color bg)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(200, 96) };
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 30);
		return b;
	}

	// ===================== 最高纪录存档 =====================

	private void LoadBest()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(BestPath) != Error.Ok)
			return;
		for (int i = 0; i < Times.Length; i++)
			_best[i] = cfg.GetValue("best", Times[i].Tag, 0).AsInt32();
	}

	private void SaveBest()
	{
		var cfg = new ConfigFile();
		for (int i = 0; i < Times.Length; i++)
			cfg.SetValue("best", Times[i].Tag, _best[i]);
		var err = cfg.Save(BestPath);
		if (err == Error.Ok)
			GD.Print($"[Nursery] best saved: {DescribeBest()}");
		else
			GD.PushError($"[Nursery] save best failed: {err}");
	}

	private string DescribeBest()
	{
		var parts = new List<string>();
		for (int i = 0; i < Times.Length; i++)
			parts.Add($"{Times[i].Tag}={(_best[i] > 0 ? _best[i].ToString() : "—")}");
		return string.Join(" ", parts);
	}

	// ===================== 内嵌节点 =====================
	//
	// 注意：C# 里凡是继承 Godot 节点类型的类（哪怕是嵌套的私有类）都必须写 partial，
	// 否则编译直接报 GD0001（Missing partial modifier ... derives from Godot.GodotObject）。

	/// <summary>棋盘的画布。逻辑全在外面的 <see cref="NurseryGame"/> 里，它只把 _Draw 转回去。</summary>
	private sealed partial class GridView : Node2D
	{
		private readonly NurseryGame _game;

		public GridView(NurseryGame game) => _game = game;

		public override void _Draw() => _game.DrawBoard(this);
	}

	/// <summary>引线 HUD 的画布（画在棋盘上方，和棋盘互不影响）。</summary>
	private sealed partial class FuseView : Node2D
	{
		private readonly NurseryGame _game;

		public FuseView(NurseryGame game) => _game = game;

		public override void _Draw() => _game.DrawFuse(this);
	}

	// ===================== 自测 =====================
	//
	// 触发方式：项目根目录放 selftest.flag，内容写 nursery，然后启动游戏
	// （首页会立刻切到本场景，本场景看到内容是自己就跑这一套）。

	private async Task RunSelfTestAsync()
	{
		GD.Print("[SELFTEST] begin (nursery)");
		await Wait(0.4);

		int fails = 0;
		_seed = 20260926; // 固定棋盘，跑出来的盘面每次一样，出问题好复现
		// 自测会把「1 分钟最高分」刷成 160（清空整盘），真去玩的人再也刷不掉，
		// 所以进门先备份玩家原有的纪录，全部验完再写回去。
		int[] keepBest = (int[])_best.Clone();

		// ① 棋盘尺寸 + 每格都是 1~9
		StartGame(0);
		bool sizeOk = _cells.Length == Rows * Cols;
		int outOfRange = 0;
		foreach (int v in _cells)
			if (v < 1 || v > 9)
				outOfRange++;
		GD.Print($"[SELFTEST] board {Rows}×{Cols} · 格子数={_cells.Length} 越界值={outOfRange} -> {sizeOk && outOfRange == 0}");
		if (!sizeOk || outOfRange != 0) fails++;

		// ①b 数字分布。这条是给「盘面清一色 1、2、3」兜底的回归断言：
		//     答案块切到 10 格的时代，9 格的块里只能是八个 1 + 一个 2，
		//     盘面又难看又难消（大片 1 意味着必须一次框住八九个格子才凑得出 10）。
		//     现在块最多 3 格，1~9 应该都出现、而且最大的那个数字也不该占到大头。
		var digitCount = new int[10];
		foreach (int v in _cells)
			digitCount[v]++;
		var dense = new List<string>();
		int worstDigit = 0, worstCount = 0;
		for (int d = 1; d <= 9; d++)
		{
			dense.Add($"{d}:{digitCount[d]}");
			if (digitCount[d] > worstCount)
			{
				worstCount = digitCount[d];
				worstDigit = d;
			}
		}
		bool allDigits = digitCount[0] == 0;
		for (int d = 1; d <= 9; d++)
			if (digitCount[d] == 0)
				allDigits = false;
		int share = worstCount * 100 / _cells.Length;
		bool balanced = share <= 22;
		GD.Print($"[SELFTEST] 数字分布: 1~9 都出现过={allDigits} 占比最高={worstDigit}({worstCount} 个, {share}%) ≤22%={balanced}");
		GD.Print($"[SELFTEST] 数字分布明细: {string.Join(" ", dense)}");
		if (!allDigits || !balanced) fails++;

		// ② 生成棋盘用的切块就是一份「标准答案」：每块和都是 10，
		//    而且按它依次框选（走真实的 ResolveSelection）能把整盘清空。
		bool eachOk = true;
		foreach (var reg in _solution)
			if (RegionSum(reg) != Target)
				eachOk = false;
		bool areaOk = true;
		foreach (var reg in _solution)
			if (reg.Area < 2 || reg.Area > MaxLeaf)
				areaOk = false;
		foreach (var reg in _solution)
			SelectRegion(reg);
		bool solvedOk = _cleared == Rows * Cols && _score == Rows * Cols;
		GD.Print($"[SELFTEST] solution: 块数={_solution.Count} 每块和=10 -> {eachOk} 块面积 2~{MaxLeaf} -> {areaOk} " +
				 $"按答案消完 {_cleared}/{Rows * Cols} 分={_score} -> {solvedOk}");
		if (!eachOk || !areaOk || !solvedOk) fails++;

		// ③ 方框内相加 ≠ 10 就不消（把答案块里的一个数字改掉，和变成 9 或 11）
		StartGame(0);
		var reg0 = _solution[0];
		int tweak = Idx(reg0.C, reg0.R);
		_cells[tweak] = _cells[tweak] <= 8 ? _cells[tweak] + 1 : _cells[tweak] - 1;
		int beforeCleared = _cleared;
		bool missResolved = !SelectRegion(reg0);
		bool missOk = missResolved && _cleared == beforeCleared && _fails.Count > 0;
		GD.Print($"[SELFTEST] 和≠10 不消除: resolved={missResolved} 已消 {beforeCleared}->{_cleared} " +
				 $"红闪={_fails.Count} -> {missOk}");
		if (!missOk) fails++;

		// ④ 分数就是消掉的数字个数：消 3 个就 +3
		StartGame(0);
		int probeCount = 0;
		foreach (var reg in _solution)
		{
			SelectRegion(reg);
			probeCount += reg.Area;
			if (probeCount >= 3)
				break;
		}
		GD.Print($"[SELFTEST] 消 {probeCount} 格 -> 智商={_score} -> {_score == probeCount}");
		if (_score != probeCount) fails++;

		// ⑤ 第一次框选就是整盘清空 → 直接判定「全部消除」并结算
		StartGame(0);
		if (_solution.Count > 0)
		{
			var whole = new Region(0, 0, Cols, Rows);
			// 整盘一定不是 10（160 格），先确认它不能一次消完
			bool wholeOk = RegionSum(whole) != Target;
			SelectRegion(whole);
			GD.Print($"[SELFTEST] 空框/整盘框不消除: 整盘和={RegionSum(whole)} -> {wholeOk && _cleared == 0}");
			if (!wholeOk || _cleared != 0) fails++;
		}

		// ⑥ 清空整盘 = 提前结束（标题「全部消除！」+ 结算可见）
		StartGame(0);
		foreach (var reg in _solution)
			SelectRegion(reg);
		await Wait(0.15);
		bool winOk = _phase == Phase.Over && _result.Visible && _cleared == Rows * Cols && _againButton.Visible;
		GD.Print($"[SELFTEST] 清空整盘: phase={_phase} cleared={_cleared} result={_result.Visible} -> {winOk}");
		if (!winOk) fails++;
		SaveShot("nursery_win");

		// ⑦ 三档时长：秒数和面板文案
		for (int i = 0; i < Times.Length; i++)
		{
			StartGame(i);
			bool ok = _totalTime == Times[i].Seconds && (int)_remain == Times[i].Seconds && _phase == Phase.Play;
			GD.Print($"[SELFTEST] time \"{Times[i].Label}\": total={_totalTime} remain={_remain} -> {ok}");
			if (!ok) fails++;
		}

		// ⑧ 时间真的在走 + 时间到自动结算
		StartGame(0);
		float before = _remain;
		await Wait(0.7);
		bool tickOk = _remain < before && _phase == Phase.Play;
		GD.Print($"[SELFTEST] 倒计时: {before:0.0} -> {_remain:0.0} -> {tickOk}");
		if (!tickOk) fails++;

		// 先消几格，让结算里的分数不是 0
		for (int i = 0; i < 3 && i < _solution.Count; i++)
			SelectRegion(_solution[i]);
		int scoreBefore = _score;
		_remain = 0.05f;
		await Wait(0.2);
		bool overOk = _phase == Phase.Over && _result.Visible && _remain == 0f && _againButton.Visible;
		GD.Print($"[SELFTEST] 时间到: phase={_phase} score={_score}(本局消了 {scoreBefore}) result={_result.Visible} -> {overOk}");
		if (!overOk || _score != scoreBefore) fails++;

		var cfg = new ConfigFile();
		int savedBest = cfg.Load(BestPath) == Error.Ok
			? cfg.GetValue("best", Times[0].Tag, -1).AsInt32()
			: -1;
		bool persisted = savedBest == _best[0];
		GD.Print($"[SELFTEST] 纪录落盘: mem={_best[0]} file={savedBest} -> {persisted}");
		if (!persisted) fails++;
		SaveShot("nursery_over");

		// 还原玩家的纪录（含文件）：自测是「秒消几格」，写进去的假成绩真去玩的人刷不掉
		System.Array.Copy(keepBest, _best, _best.Length);
		SaveBest();

		// ⑨ 面板：三档按钮文案 + 「换时长」回面板
		ShowSetup();
		await Wait(0.1);
		bool setupOk = _setup.Visible && _phase == Phase.Setup &&
					   _timeSubs[0].Text.Contains("60 秒") && _timeBests[0].Text.Contains("最高");
		GD.Print($"[SELFTEST] setup panel: visible={_setup.Visible} sub0=\"{_timeSubs[0].Text}\" " +
				 $"best0=\"{_timeBests[0].Text}\" -> {setupOk}");
		if (!setupOk) fails++;
		SaveShot("nursery_setup");

		// ⑩ 真实 GUI 链路：在面板上点「2 分钟」开局，再拖一个方框消掉它
		PushGuiClick(_timeButtons[1].GetGlobalRect().GetCenter());
		await Wait(0.2);
		bool startByClickOk = !_setup.Visible && _phase == Phase.Play &&
							  _totalTime == Times[1].Seconds && _cleared == 0;
		GD.Print($"[SELFTEST] gui 点「2 分钟」开局: setupVisible={_setup.Visible} phase={_phase} " +
				 $"total={_totalTime} -> {startByClickOk}");
		if (!startByClickOk) fails++;

		// ⑪ 系统入口（窗口像素 + Input.parse_input_event）拖一个答案块：按下 → 移动 → 抬手
		var regA = _solution[0];
		var from = CellCenter(regA.C, regA.R);
		var to = CellCenter(regA.C + regA.W - 1, regA.R + regA.H - 1);
		int beforeOs = _cleared;
		PushOsDrag(from, to);
		await Wait(0.2);
		bool osDragOk = _cleared == beforeOs + regA.Area;
		GD.Print($"[SELFTEST] os 拖拽消除: 已消 {beforeOs}->{_cleared}（应 +{regA.Area}） -> {osDragOk}");
		if (!osDragOk) fails++;

		// ⑫ 模拟事件必须被过滤掉：否则一次拖拽会被结算两遍（第二次框到的是空洞，和=0）
		var emu = new InputEventScreenTouch { Position = from, Pressed = true, Device = -1 };
		bool emuIgnored = IsEmulated(emu);
		GD.Print($"[SELFTEST] emulated event filtered: {emuIgnored}");
		if (!emuIgnored) fails++;

		// ⑬ 空格子凑不出 10：拖一个已经被消掉的区域，什么也不该发生
		int beforeHole = _cleared;
		var holeFrom = CellCenter(regA.C, regA.R);
		var holeTo = CellCenter(regA.C + regA.W - 1, regA.R + regA.H - 1);
		PressAt(holeFrom);
		await Wait(0.05);
		DragTo(holeTo);
		await Wait(0.05);
		ReleaseAt(holeTo);
		await Wait(0.1);
		bool holeOk = _cleared == beforeHole;
		GD.Print($"[SELFTEST] 空洞框选不消除: 已消 {beforeHole}->{_cleared} -> {holeOk}");
		if (!holeOk) fails++;

		// ⑭ 棋盘算得对：装得进可用区、格子不小于下限
		bool gridOk = _tile >= MinTile && _tile * Cols <= StageW - PadX * 2f + 0.5f &&
					  _origin.Y >= BoardTop - 0.5f && _origin.Y + _tile * Rows <= BoardBottom + 0.5f;
		GD.Print($"[SELFTEST] grid fits: tile={_tile} origin={_origin} -> {gridOk}");
		if (!gridOk) fails++;

		// ⑮ 背景真的铺满了吗（露出 0.3 灰的清屏色就说明背景没覆盖整屏）
		var shot = GetViewport().GetTexture().GetImage();
		var px = new Vector2I((int)(shot.GetWidth() * 0.01f), (int)(shot.GetHeight() * 0.5f));
		var col = shot.GetPixel(px.X, px.Y);
		bool bgOk = !GameArt.IsClearColor(col);
		GD.Print($"[SELFTEST] background covers screen: pixel{px}={col} -> {bgOk}");
		if (!bgOk) fails++;

		// ⑮b 引线真的画出来了吗：取「引线中段」那个像素，必须是暖橙色。
		//      光验数据/逻辑的断言盯不住「画布根本没重画」这类问题 ——
		//      引线就曾经整根消失过（QueueRedraw 打在了父节点上，子画布再没重画过）。
		//      截图是窗口像素，所以逻辑坐标要按 设计分辨率 → 截图尺寸 换算一下。
		StartGame(2);
		await Wait(0.2);
		var fuseShot = GetViewport().GetTexture().GetImage();
		float kx = fuseShot.GetWidth() / DesignW;
		float ky = fuseShot.GetHeight() / DesignH;
		var ropePx = new Vector2I((int)((FuseLeft + 120f) * kx), (int)(FuseY * ky));
		var ropeCol = fuseShot.GetPixel(ropePx.X, ropePx.Y);
		bool fuseOk = ropeCol.R > 0.6f && ropeCol.R > ropeCol.B * 1.6f && ropeCol.G > 0.35f;
		GD.Print($"[SELFTEST] 引线可见: pixel{ropePx}={ropeCol} -> {fuseOk}");
		if (!fuseOk) fails++;

		// ⑯ 正常玩一会儿，截一张中局盘面（消掉一半答案块，剩下的留着看数字）
		StartGame(2);
		for (int i = 0; i < _solution.Count / 2; i++)
			SelectRegion(_solution[i]);
		await Wait(0.3);
		SaveShot("nursery_board");

		GD.Print(fails == 0 ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} 项)");
		await Wait(0.4);
		GetTree().Quit();
	}

	// ---------- 自测用的小工具 ----------

	/// <summary>用真实的「框选 → 结算」路径把一块区域消掉，返回是否真的消掉了。</summary>
	private bool SelectRegion(Region reg)
	{
		_anchor = Idx(reg.C, reg.R);
		_cur = Idx(reg.C + reg.W - 1, reg.R + reg.H - 1);
		return ResolveSelection();
	}

	private int RegionSum(Region reg)
	{
		int s = 0;
		for (int r = reg.R; r < reg.R + reg.H; r++)
			for (int c = reg.C; c < reg.C + reg.W; c++)
				s += _cells[Idx(c, r)];
		return s;
	}

	private Vector2 CellCenter(int c, int r)
		=> _origin + new Vector2((c + 0.5f) * _tile, (r + 0.5f) * _tile);

	/// <summary>自测里模拟输入：按下 / 拖动 / 抬手（直接调处理函数，绕过 GUI 命中测试）。</summary>
	private void PressAt(Vector2 pos)
		=> _Input(new InputEventScreenTouch { Position = pos, Pressed = true });

	private void ReleaseAt(Vector2 pos)
		=> _Input(new InputEventScreenTouch { Position = pos, Pressed = false });

	/// <summary>
	/// 推一次「真·拖拽」给视口：按下 + 移动 + 抬手，并连引擎的模拟触摸一起推，
	/// 和开着 mouse→touch 的运行时收到的序列一致（真鼠标 device=0 + 模拟触摸 device=-1）。
	/// 走这条路的输入会先经过 GUI 命中测试，能测出「事件根本没送到棋盘」的问题。
	/// </summary>
	private void PushGuiClick(Vector2 pos) => PushGuiDrag(pos, pos);

	private void PushGuiDrag(Vector2 from, Vector2 to)
	{
		var vp = GetViewport();
		vp.PushInput(new InputEventMouseButton
		{
			Position = from, GlobalPosition = from, ButtonIndex = MouseButton.Left,
			Pressed = true, Device = 0,
		}, true);
		vp.PushInput(new InputEventScreenTouch { Position = from, Pressed = true, Device = -1 }, true);
		vp.PushInput(new InputEventMouseMotion { Position = to, GlobalPosition = to, Device = 0 }, true);
		vp.PushInput(new InputEventScreenDrag { Position = to, Device = -1 }, true);
		vp.PushInput(new InputEventMouseButton
		{
			Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Left,
			Pressed = false, Device = 0,
		}, true);
		vp.PushInput(new InputEventScreenTouch { Position = to, Pressed = false, Device = -1 }, true);
	}

	/// <summary>
	/// 再走一遍**操作系统的入口**：窗口像素坐标 + Input.parse_input_event。
	/// 这条才是真玩家按下鼠标时事件进引擎的路，也是唯一会触发引擎「模拟触摸」的路，
	/// 正好顺带验证 device = -1 的过滤在真实链路里也生效。
	/// </summary>
	private void PushOsDrag(Vector2 from, Vector2 to)
	{
		var tf = GetViewport().GetFinalTransform();
		var wFrom = tf * from;
		var wTo = tf * to;
		Input.ParseInputEvent(new InputEventMouseMotion { Position = wFrom, GlobalPosition = wFrom });
		Input.ParseInputEvent(new InputEventMouseButton
		{
			Position = wFrom, GlobalPosition = wFrom, ButtonIndex = MouseButton.Left, Pressed = true,
		});
		Input.ParseInputEvent(new InputEventMouseMotion { Position = wTo, GlobalPosition = wTo });
		Input.ParseInputEvent(new InputEventMouseButton
		{
			Position = wTo, GlobalPosition = wTo, ButtonIndex = MouseButton.Left, Pressed = false,
		});
	}

	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Nursery] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}
}
