#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 蜘蛛纸牌 —— 两副牌 104 张、10 个牌列、余牌分 5 次发。
///
/// 规则（和 Windows 那个一模一样）：
/// <list type="bullet">
/// <item>开局 54 张：前 4 列各 6 张、后 6 列各 5 张，每列只有最上面一张翻开；</item>
/// <item>余下 50 张是牌库，每次给每列发一张（共 10 张），一共 5 次；有空列时不许发；</item>
/// <item>放牌只看点数：比目标小 1 就行，花色不限；</item>
/// <item>搬牌时被拿起的那一段必须是**同花色、点数连续递减**、并且一直到该列末尾；</item>
/// <item>空列可以放任意单张或整段；</item>
/// <item>某列末尾凑出同花色的 K→A 会被自动收走，收满 8 组（蜘蛛的 8 条腿）即胜利。</item>
/// </list>
///
/// 难度只改「用几种花色」：简单全黑桃、普通黑桃+红桃、困难四种花色各两套。
/// 规则完全一样，花色越多越难凑出同花连续段。
///
/// 结构上和扫雷一致：一个脚本装下全部逻辑与绘制，场景文件里只有骨架节点；
/// 所有图形都是 _Draw 现画的，花色也是矢量画出来的（不指望字体里有 ♠♥♣♦ 这几个码位）。
/// </summary>
public partial class SpiderGame : Control
{
	// ================= 可调参数 =================

	private const int Cols = 10;              // 牌列数
	private const int Total = 104;            // 两副牌
	private const int SetsToWin = 8;          // 收齐 8 组 K→A 才算赢
	private const int DealSize = 10;          // 每次发牌发 10 张：每列一张
	private const int MaxStockDeals = 5;      // 余牌只够发 5 次

	private const int SuitSpade = 0, SuitHeart = 1, SuitClub = 2, SuitDiamond = 3;

	private const float PadX = 12f;           // 牌列左右留白
	private const float Gap = 5f;             // 牌列间距
	private const float CardRatio = 1.45f;    // 牌的高 / 宽
	private const float DownOffset = 13f;     // 背面朝上的牌露出多少
	private const float UpOffset = 32f;       // 正面朝上的牌露出多少
	private const float BoardTop = 216f;      // 牌列可用区上边界（720×1280 逻辑分辨率下的绝对值）
	private const float BoardBottom = 1076f;  // 牌列可用区下边界（下面留给撤销/提示/发牌）
	private const float FoundationY = 146f;   // 成组区那一行
	private const float FoundationH = 58f;
	private const float FoundationW = 40f;

	private const int StartScore = 500;       // 开局 500 分
	private const int PerMoveCost = 1;        // 每走一步 -1 分
	private const int PerSetBonus = 100;      // 每收一组 +100 分

	private const float TapPx = 12f;              // 位移小于这么多像素算「点一下」而不是拖
	private const float LongPressSeconds = 0.45f; // 按住不动这么久 = 看提示
	private const float HintSeconds = 2.2f;       // 提示高亮持续多久
	private const float ToastSeconds = 1.8f;
	private const float DealStagger = 0.013f;     // 发牌动画：相邻两张牌差这么多秒出发
	private const float FlyDur = 0.28f;           // 单张牌飞行时长

	private const string BestPath = "user://spider_best.cfg";

	/// <summary>自测用固定随机种子：牌局每次跑都一模一样，出问题好复现。</summary>
	private const int SelfTestSeed = 20260924;

	/// <summary>一档难度。</summary>
	private sealed class Level
	{
		public string Name = "";
		public int SuitCount = 1;             // 用几种花色
		public Color Tint = Colors.White;
		public string Detail = "";            // 面板上的一行小字
	}

	private static readonly Level[] Levels =
	{
		new Level { Name = "简单", SuitCount = 1, Tint = new Color("#7ee787"),
			Detail = "全黑桃 · 8 组同花序列" },
		new Level { Name = "普通", SuitCount = 2, Tint = new Color("#ffd77a"),
			Detail = "黑桃 + 红桃 · 各 4 组" },
		new Level { Name = "困难", SuitCount = 4, Tint = new Color("#ff8f6b"),
			Detail = "四种花色 · 各 2 组" },
	};

	// ================= 牌局状态 =================

	private enum Phase { Setup, Play, Win, Stuck }

	private Phase _phase = Phase.Setup;
	private int _levelIndex;
	private int _seed;

	private readonly int[] _suit = new int[Total];
	private readonly int[] _rank = new int[Total];        // 1=A … 13=K
	private readonly List<int>[] _cols = new List<int>[Cols];
	private readonly bool[] _up = new bool[Total];        // 这张牌是不是正面朝上
	private readonly List<int> _stock = new();            // 牌库（从末尾往外发）
	private readonly List<int> _foundation = new();       // 已收走的花色（最多 8 个）
	private readonly List<int> _dealOrder = new();        // 开局发牌顺序（发牌动画用）
	private readonly float[][] _ys = new float[Cols][];   // 每列每张牌相对 BoardTop 的 y
	private readonly List<Snap> _history = new();

	private int _score, _moves;
	private float _cw = 65f, _ch = 94f;                   // 牌宽 / 牌高（按屏宽算出来）
	private Vector2 _flyFrom;                             // 发牌动画的起点（发牌按钮那一带）

	// 当前这一局的进行态
	private bool _dealing;                                // 发牌动画进行中（此间不接受操作）
	private Drag? _drag;
	private int _hintCol = -1, _hintIdx = -1, _hintDst = -1;
	private float _hintLeft, _longPress;
	private string _toast = "";
	private float _toastLeft;
	private float _time;                                  // 累计时间（提示呼吸用）
	private int _hoverCol = -1, _hoverIdx = -1;

	/// <summary>正被拖着走的那一段。</summary>
	private sealed class Drag
	{
		public int Col;
		public int Idx;
		public Vector2 GrabOffset;     // 按下时指针相对该牌原点的偏移
		public Vector2 Pointer;
		public bool Moved;             // 位移超过 TapPx 就变成「拖」而不是「点」
	}

	/// <summary>一张正在飞的牌（开局发牌飞入 / 收组飞走）。</summary>
	private sealed class Air
	{
		public int Id;
		public Vector2 From, To;
		public float T, Dur, Arc;
		public float ShrinkTo = 1f;    // 收组时从 1 缩到 0.42，飞进成组槽
		public bool FaceUp = true;
	}

	private readonly Dictionary<int, Air> _air = new();

	/// <summary>一步棋之前的状态快照（撤销用）。</summary>
	private sealed class Snap
	{
		public List<int>[] Cols = System.Array.Empty<List<int>>();
		public bool[] Up = System.Array.Empty<bool>();
		public List<int> Stock = new();
		public List<int> Foundation = new();
		public int Score, Moves;
	}

	// ================= 画牌用的样式盒（缓存，_Draw 里不能每帧 new） =================

	private StyleBoxFlat _sbFace = null!;        // 正面：圆角白卡
	private StyleBoxFlat _sbFaceStrip = null!;   // 正面但只露一条：上圆角、下直角
	private StyleBoxFlat _sbBack = null!;        // 背面
	private StyleBoxFlat _sbBackStrip = null!;
	private StyleBoxFlat _sbSlot = null!;        // 空列底槽
	private StyleBoxFlat _sbGlowGood = null!;    // 能放：琥珀色高亮
	private StyleBoxFlat _sbGlowBad = null!;     // 放不下：红色
	private StyleBoxFlat _sbShadow = null!;      // 拖起来时的投影

	// ================= 场景节点 =================

	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _board = null!;
	private TableView _table = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _setupButton = null!;
	private Button _restartButton = null!;
	private HBoxContainer _hud = null!;
	private Label _scoreLabel = null!;
	private Label _midLabel = null!;
	private Label _setsLabel = null!;
	private Button _undoButton = null!;
	private Button _hintButton = null!;
	private Button _dealButton = null!;
	private Label _hintLabel = null!;
	private Control _setup = null!;
	private Button _resumeButton = null!;
	private readonly Button[] _levelButtons = new Button[3];
	private readonly Label[] _levelSubs = new Label[3];
	private readonly Label[] _levelBests = new Label[3];
	private ColorRect _result = null!;
	private Label _resultTitle = null!;
	private Label _resultInfo = null!;
	private Label _resultExtra = null!;
	private Button _againButton = null!;
	private Button _resultSetupButton = null!;

	private Font? _font;
	private readonly int[] _best = new int[3];

	// HUD 文本缓存：只在数值真变了才重建字符串
	private int _vScore = int.MinValue, _vMoves = int.MinValue, _vSets = int.MinValue, _vStock = int.MinValue;

	private float StageW => Size.X > 0f ? Size.X : 720f;
	private float StageH => Size.Y > 0f ? Size.Y : 1280f;

	// ================= 生命周期 =================

	public override void _Ready()
	{
		_stage = GetNode<Control>("Stage");
		_background = GetNode<TextureRect>("Stage/Background");
		_board = GetNode<Node2D>("Stage/Board");
		_ui = GetNode<Control>("UI");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_setupButton = GetNode<Button>("UI/TopBar/SetupButton");
		_restartButton = GetNode<Button>("UI/TopBar/RestartButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		// 根节点铺满全屏，默认的 MouseFilter = Stop 会把落在空白处的点击全「认领」走，
		// 而牌桌是 Node2D 画的、靠代码自己接事件 —— 被认领走就等于点不动，必须让路。
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = GameArt.MakeUiTheme();
		_font = GameArt.UiFont;

		_background.Texture = GameArt.VerticalGradient(new Color("#07140f"), new Color("#123a2a"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		for (int c = 0; c < Cols; c++)
			_cols[c] = new List<int>();

		_table = new TableView(this);
		_board.AddChild(_table);

		BuildStyleBoxes();
		BuildTopBar();
		BuildHud();
		BuildBottomBar();
		BuildSetup();
		BuildResult();
		LoadBest();

		Layout();
		GetViewport().SizeChanged += Layout;

		GD.Print($"[Spider] ready. best={DescribeBest()} selftest={SelftestFlag.Describe()}");

		if (SelftestFlag.Read() == SelftestFlag.TokenSpider)
			_ = RunSelfTestAsync();
		else
			ShowSetup();
	}

	private void Layout()
	{
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」，否则只在左上角画一小块，
		// 其余露出视口清屏色（0.3 灰）—— 自测里有像素探针专门守这条。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		ComputeGeometry();
		RecomputeOffsets();
	}

	/// <summary>按屏宽算牌宽牌高，以及发牌动画的起点。</summary>
	private void ComputeGeometry()
	{
		_cw = Mathf.Floor((StageW - PadX * 2f - Gap * (Cols - 1)) / Cols);
		_ch = Mathf.Round(_cw * CardRatio);
		_flyFrom = new Vector2(StageW * 0.5f, StageH - 118f);
	}

	/// <summary>
	/// 算每张牌在列里的 y。牌列太长时按比例压缩间距，保证整列不越界 ——
	/// 蜘蛛纸牌后期一列能堆二三十张，不压缩就会顶出屏幕。
	/// </summary>
	private void RecomputeOffsets()
	{
		float avail = Mathf.Max(_ch, BoardBottom - BoardTop - _ch);
		for (int c = 0; c < Cols; c++)
		{
			var list = _cols[c];
			int n = list.Count;
			if (_ys[c] == null || _ys[c].Length < n)
				_ys[c] = new float[Mathf.Max(n, 8)];

			float sum = 0f;
			for (int i = 0; i < n - 1; i++)
				sum += _up[list[i]] ? UpOffset : DownOffset;

			float k = sum > avail && sum > 0.001f ? avail / sum : 1f;
			float y = 0f;
			for (int i = 0; i < n; i++)
			{
				_ys[c][i] = y;
				if (i < n - 1)
					y += (_up[list[i]] ? UpOffset : DownOffset) * k;
			}
		}
	}

	private void Redraw() => _table.QueueRedraw();

	private void BuildStyleBoxes()
	{
		_sbFace = CardBox(new Color("#fbfcff"), new Color("#b9c4dc"), 7f);
		_sbFaceStrip = CardBox(new Color("#fbfcff"), new Color("#b9c4dc"), 7f, true);
		_sbBack = CardBox(new Color("#2b3f7d"), new Color("#18254d"), 7f);
		_sbBackStrip = CardBox(new Color("#2b3f7d"), new Color("#18254d"), 7f, true);
		_sbSlot = CardBox(new Color(1f, 1f, 1f, 0.05f), new Color(1f, 1f, 1f, 0.22f), 7f);
		_sbGlowGood = CardBox(new Color(0f, 0f, 0f, 0f), new Color("#ffd166"), 8f, false, 4f);
		_sbGlowBad = CardBox(new Color(0f, 0f, 0f, 0f), new Color("#ff6b81"), 8f, false, 4f);
		_sbShadow = CardBox(new Color(0f, 0f, 0f, 0.34f), new Color(0f, 0f, 0f, 0f), 8f, false, 0f);
	}

	/// <summary>造一个卡牌用的样式盒。<paramref name="topOnly"/> = 只圆上边两个角（牌列里被压着的牌只露一条）。</summary>
	private static StyleBoxFlat CardBox(Color bg, Color border, float radius, bool topOnly = false, float bw = 2.5f)
	{
		var sb = new StyleBoxFlat { BgColor = bg };
		sb.CornerRadiusTopLeft = sb.CornerRadiusTopRight = (int)radius;
		sb.CornerRadiusBottomLeft = sb.CornerRadiusBottomRight = topOnly ? 0 : (int)radius;
		sb.BorderColor = border;
		sb.BorderWidthLeft = sb.BorderWidthRight = sb.BorderWidthTop = (int)bw;
		sb.BorderWidthBottom = topOnly ? 0 : (int)bw;
		return sb;
	}

	// ================= 发牌与开局 =================

	/// <summary>按难度造牌堆：简单 8 套黑桃、普通 4+4、困难 每种花色 2 套。</summary>
	private void BuildDeck(int suitCount)
	{
		int k = 0;
		for (int set = 0; set < 8; set++)
		{
			int suit = suitCount switch
			{
				1 => SuitSpade,
				2 => set % 2 == 0 ? SuitSpade : SuitHeart,
				_ => set % 4,
			};
			for (int rank = 1; rank <= 13; rank++)
			{
				_suit[k] = suit;
				_rank[k] = rank;
				k++;
			}
		}
	}

	private void Shuffle(int seed)
	{
		var rng = new System.Random(seed != 0 ? seed : (int)(Time.GetTicksMsec() & 0x7fffffff));
		for (int i = Total - 1; i > 0; i--)
		{
			int j = rng.Next(i + 1);
			(_suit[i], _suit[j]) = (_suit[j], _suit[i]);
			(_rank[i], _rank[j]) = (_rank[j], _rank[i]);
		}
	}

	/// <summary>
	/// 发牌：一轮一轮往 10 列上发，最后一轮只有前 4 列有牌 ——
	/// 这样正好是「前 4 列各 6 张、后 6 列各 5 张」共 54 张，剩下 50 张进牌库（够发 5 次）。
	/// </summary>
	private void Deal()
	{
		for (int c = 0; c < Cols; c++)
		{
			_cols[c].Clear();
			_ys[c] = null!;
		}
		_stock.Clear();
		_foundation.Clear();
		_dealOrder.Clear();
		System.Array.Clear(_up);

		int k = 0;
		for (int round = 0; round < 6; round++)
		{
			for (int c = 0; c < Cols; c++)
			{
				if (round == 5 && c >= 4)
					continue;                 // 最后一轮只发前 4 列 → 那 4 列各多一张
				_cols[c].Add(k);
				_dealOrder.Add(k);
				_up[k] = false;
				k++;
			}
		}
		for (int c = 0; c < Cols; c++)
			_up[_cols[c][^1]] = true;         // 每列只有最上面一张是明牌

		for (; k < Total; k++)
			_stock.Add(k);
	}

	private void StartGame(int levelIndex, bool animate = true, int seed = 0)
	{
		_levelIndex = Mathf.Clamp(levelIndex, 0, Levels.Length - 1);
		_seed = seed;
		_phase = Phase.Play;
		_setup.Visible = false;
		_result.Visible = false;

		_history.Clear();
		_air.Clear();
		_drag = null;
		_hintCol = _hintIdx = _hintDst = -1;
		_hintLeft = 0f;
		_toast = "";
		_toastLeft = 0f;
		_score = StartScore;
		_moves = 0;

		BuildDeck(Levels[_levelIndex].SuitCount);
		Shuffle(seed);
		Deal();
		RecomputeOffsets();

		_dealing = animate;
		if (animate)
			StartDealAnimation();

		UpdateHud();
		UpdateHintText();
		Redraw();
	}

	/// <summary>把 54 张牌从「发牌按钮」那一带一张张飞进各自的牌位。</summary>
	private void StartDealAnimation()
	{
		_air.Clear();
		for (int i = 0; i < _dealOrder.Count; i++)
		{
			int id = _dealOrder[i];
			var (c, idx) = Locate(id);
			if (c < 0)
				continue;
			_air[id] = new Air
			{
				Id = id,
				From = _flyFrom + new Vector2((i % 10 - 4.5f) * 3f, 0f),
				To = CardPos(c, idx),
				T = -i * DealStagger,             // 负的出发时间 = 延迟出发
				Dur = FlyDur,
				Arc = 60f + i % 7 * 6f,
				FaceUp = _up[id],
			};
		}
	}

	// ================= 查询 =================

	/// <summary>某张牌现在在哪个牌列的哪一位；不在列里返回 (-1,-1)。</summary>
	private (int Col, int Idx) Locate(int id)
	{
		for (int c = 0; c < Cols; c++)
		{
			int i = _cols[c].IndexOf(id);
			if (i >= 0)
				return (c, i);
		}
		return (-1, -1);
	}

	private Vector2 CardPos(int c, int i)
		=> new(PadX + c * (_cw + Gap), BoardTop + _ys[c][i]);

	private Rect2 CardRect(int c, int i)
		=> new(CardPos(c, i), new Vector2(_cw, _ch));

	/// <summary>
	/// 这张牌「能露出来多少」。牌列里被压着的牌只剩最上面一条可见，
	/// 画牌和命中测试都得按这个高度来，否则点上面那张会误判成下面那张。
	/// </summary>
	private float VisibleH(int c, int i)
	{
		var list = _cols[c];
		if (i == list.Count - 1)
			return _ch;
		return Mathf.Min(_ch, _ys[c][i + 1] - _ys[c][i]);
	}

	/// <summary>这一点落在哪张牌上（不管能不能搬），没有则 (-1,-1)。</summary>
	private (int Col, int Idx) CardAt(Vector2 pos)
	{
		if (_cw <= 0f)
			return (-1, -1);
		for (int c = 0; c < Cols; c++)
		{
			float x0 = PadX + c * (_cw + Gap);
			if (pos.X < x0 || pos.X > x0 + _cw)
				continue;
			var list = _cols[c];
			for (int i = list.Count - 1; i >= 0; i--)
			{
				float y0 = BoardTop + _ys[c][i];
				if (pos.Y >= y0 && pos.Y <= y0 + VisibleH(c, i))
					return (c, i);
			}
		}
		return (-1, -1);
	}

	/// <summary>屏幕横坐标落在哪一列（拖拽时判断想放到哪）。</summary>
	private int ColumnAtX(float x)
	{
		int c = Mathf.FloorToInt((x - PadX) / (_cw + Gap));
		return Mathf.Clamp(c, 0, Cols - 1);
	}

	/// <summary>列末那张牌的中心（自测推事件、以及画高亮都用它）。</summary>
	private Vector2 LastCardCenter(int c)
	{
		int i = _cols[c].Count - 1;
		if (i < 0)
			return new Vector2(PadX + c * (_cw + Gap) + _cw * 0.5f, BoardTop + _ch * 0.5f);
		return new Vector2(CardPos(c, i).X + _cw * 0.5f, CardPos(c, i).Y + _ch * 0.5f);
	}

	/// <summary>从 idx 开始到列末是不是一段合法（同花色连续递减、全明牌）的可搬段。</summary>
	private bool IsRunStart(int c, int idx)
	{
		var list = _cols[c];
		if (idx < 0 || idx >= list.Count)
			return false;
		for (int i = idx; i < list.Count; i++)
		{
			if (!_up[list[i]])
				return false;
			if (i > idx && (_suit[list[i]] != _suit[list[i - 1]] || _rank[list[i]] != _rank[list[i - 1]] - 1))
				return false;
		}
		return true;
	}

	/// <summary>点数能不能接上：目标列为空 → 随便放；否则目标末尾必须正好大 1（花色不限）。</summary>
	private bool CanPlace(int movingCard, int dstCol)
	{
		var list = _cols[dstCol];
		if (list.Count == 0)
			return true;
		return _rank[list[^1]] == _rank[movingCard] + 1;
	}

	// ================= 走子 =================

	private bool TryMove(int fromCol, int idx, int toCol)
	{
		if (_phase != Phase.Play || fromCol == toCol)
			return false;
		if (fromCol < 0 || fromCol >= Cols || toCol < 0 || toCol >= Cols)
			return false;
		if (!IsRunStart(fromCol, idx))
			return false;
		if (!CanPlace(_cols[fromCol][idx], toCol))
			return false;

		PushHistory();
		MoveRun(fromCol, idx, toCol);
		_moves++;
		_score -= PerMoveCost;
		// 顺序不能反：目标列刚刚变长（也可能有牌被翻开），_ys 还是按旧长度分配的，
		// 而 CollectRuns 要取新牌当前的位置做飞行动画的起点 —— 先重算才不会越界。
		RecomputeOffsets();
		CollectRuns();
		RecomputeOffsets();   // 收组把牌摘走了，位置又得重排一遍
		UpdateHud();
		CheckEnd();
		Redraw();
		return true;
	}

	private void MoveRun(int fromCol, int idx, int toCol)
	{
		var src = _cols[fromCol];
		var moving = src.GetRange(idx, src.Count - idx);
		src.RemoveRange(idx, moving.Count);
		_cols[toCol].AddRange(moving);

		// 搬走之后露出来的那张自己翻开
		if (src.Count > 0 && !_up[src[^1]])
			_up[src[^1]] = true;
	}

	/// <summary>从牌库发一轮：每列一张，共 10 张。</summary>
	private bool DealStock()
	{
		if (_phase != Phase.Play)
			return false;
		if (_stock.Count == 0)
		{
			Toast("没有余牌了");
			return false;
		}
		if (AnyEmptyColumn())
		{
			Toast("有空列时不能发牌，先把空列填上");
			return false;
		}

		PushHistory();
		for (int c = 0; c < Cols; c++)
		{
			int id = _stock[^1];
			_stock.RemoveAt(_stock.Count - 1);
			_up[id] = true;
			_cols[c].Add(id);
			_air[id] = new Air
			{
				Id = id,
				From = _flyFrom + new Vector2((c - 4.5f) * 3f, 0f),
				To = Vector2.Zero,                 // 等 RecomputeOffsets 之后才知道落点
				T = -c * 0.012f,
				Dur = FlyDur,
				Arc = 50f + c * 4f,
				FaceUp = true,
			};
		}
		_moves++;
		_score -= PerMoveCost;
		RecomputeOffsets();
		foreach (int id in new List<int>(_air.Keys))
		{
			var (c, idx) = Locate(id);
			if (c >= 0)
				_air[id].To = CardPos(c, idx);
		}
		CollectRuns();
		RecomputeOffsets();
		UpdateHud();
		CheckEnd();
		Redraw();
		return true;
	}

	/// <summary>
	/// 收组：某列末尾 13 张正好是同花色的 K→A 就整段收走（+100 分）。
	/// 返回这一轮收了几组。
	/// </summary>
	private int CollectRuns()
	{
		int got = 0;
		for (int c = 0; c < Cols; c++)
		{
			var list = _cols[c];
			if (list.Count < 13)
				continue;
			int start = list.Count - 13;
			int suit = _suit[list[start]];
			bool ok = true;
			for (int i = 0; i < 13 && ok; i++)
			{
				int id = list[start + i];
				if (!_up[id] || _suit[id] != suit || _rank[id] != 13 - i)
					ok = false;
			}
			if (!ok)
				continue;

			// 先把这 13 张的当前位置记下来（它们要飞向成组槽），再摘掉
			for (int i = 0; i < 13; i++)
			{
				int id = list[start + i];
				_air[id] = new Air
				{
					Id = id,
					From = CardPos(c, start + i),
					To = FoundationSlotCenter(_foundation.Count),
					T = -i * 0.024f,
					Dur = FlyDur + 0.12f,
					Arc = 30f,
					ShrinkTo = 0.42f,
					FaceUp = true,
				};
			}
			list.RemoveRange(start, 13);
			_foundation.Add(suit);
			_score += PerSetBonus;
			got++;

			if (list.Count > 0 && !_up[list[^1]])
				_up[list[^1]] = true;
		}
		if (got > 0)
			UpdateHud();
		return got;
	}

	private bool AnyEmptyColumn()
	{
		for (int c = 0; c < Cols; c++)
			if (_cols[c].Count == 0)
				return true;
		return false;
	}

	private bool WholeColumn(int c, int idx) => idx == 0 && _cols[c].Count > 0;

	/// <summary>
	/// 把某张牌「随手一放」时挑个最好的落点：同花色 &gt; 别的花色 &gt; 空列。
	/// 找不到返回 -1。整列搬进空列这种原地打转的走法不算。
	/// </summary>
	private int FindBestDestination(int col, int idx)
	{
		int movingId = _cols[col][idx];
		int best = -1, bestPri = 99;
		for (int d = 0; d < Cols; d++)
		{
			if (d == col)
				continue;
			var list = _cols[d];
			int pri;
			if (list.Count == 0)
			{
				if (WholeColumn(col, idx))
					continue;
				pri = 3;
			}
			else if (_rank[list[^1]] != _rank[movingId] + 1)
			{
				continue;
			}
			else
			{
				pri = _suit[list[^1]] == _suit[movingId] ? 1 : 2;
			}
			if (pri < bestPri)
			{
				bestPri = pri;
				best = d;
			}
		}
		return best;
	}

	/// <summary>随便找一个能走的步子（提示用）。找不到返回 null。</summary>
	private (int Col, int Idx, int Dst)? FindHint()
	{
		(int Col, int Idx, int Dst)? fallback = null;
		for (int c = 0; c < Cols; c++)
		{
			int n = _cols[c].Count;
			for (int idx = 0; idx < n; idx++)
			{
				if (!IsRunStart(c, idx))
					continue;
				int d = FindBestDestination(c, idx);
				if (d < 0)
					continue;
				bool sameSuit = _cols[d].Count > 0 && _suit[_cols[d][^1]] == _suit[_cols[c][idx]];
				// 同花色的接法最值钱，直接用它；否则先记一个备用
				if (sameSuit)
					return (c, idx, d);
				fallback ??= (c, idx, d);
			}
		}
		return fallback;
	}

	private bool HasAnyMove()
	{
		for (int c = 0; c < Cols; c++)
		{
			int n = _cols[c].Count;
			for (int idx = 0; idx < n; idx++)
			{
				if (!IsRunStart(c, idx))
					continue;
				if (FindBestDestination(c, idx) >= 0)
					return true;
			}
		}
		return false;
	}

	private void CheckEnd()
	{
		if (_foundation.Count >= SetsToWin)
		{
			_phase = Phase.Win;
			bool record = _score > _best[_levelIndex];
			if (record)
			{
				_best[_levelIndex] = _score;
				SaveBest();
			}
			ShowResult("通关！", $"{Levels[_levelIndex].Name} · 得分 {_score}",
				$"走了 {_moves} 步 · 8 组全部收走" + (record ? " · ★ 新纪录" : ""));
			return;
		}
		if (_stock.Count == 0 && !HasAnyMove())
		{
			_phase = Phase.Stuck;
			ShowResult("走不动了", $"{Levels[_levelIndex].Name} · 得分 {_score}",
				$"已收 {_foundation.Count}/8 组 · 余牌已经发完");
		}
	}

	// ================= 撤销 =================

	private void PushHistory()
	{
		var snap = new Snap
		{
			Cols = new List<int>[Cols],
			Up = new bool[Total],
			Stock = new List<int>(_stock),
			Foundation = new List<int>(_foundation),
			Score = _score,
			Moves = _moves,
		};
		for (int c = 0; c < Cols; c++)
			snap.Cols[c] = new List<int>(_cols[c]);
		System.Array.Copy(_up, snap.Up, Total);
		_history.Add(snap);
		if (_history.Count > 200)
			_history.RemoveAt(0);
	}

	private bool Undo()
	{
		if (_phase != Phase.Play && _phase != Phase.Stuck)
			return false;
		if (_history.Count == 0)
		{
			Toast("已经退到头了");
			return false;
		}
		var snap = _history[^1];
		_history.RemoveAt(_history.Count - 1);

		for (int c = 0; c < Cols; c++)
		{
			_cols[c].Clear();
			_cols[c].AddRange(snap.Cols[c]);
		}
		System.Array.Copy(snap.Up, _up, Total);
		_stock.Clear();
		_stock.AddRange(snap.Stock);
		_foundation.Clear();
		_foundation.AddRange(snap.Foundation);
		_score = snap.Score;
		_moves = snap.Moves;

		_air.Clear();
		_drag = null;
		_dealing = false;
		_hintLeft = 0f;
		_hintCol = _hintIdx = _hintDst = -1;
		_phase = Phase.Play;
		_result.Visible = false;
		RecomputeOffsets();
		UpdateHud();
		UpdateHintText();
		Redraw();
		return true;
	}

	// ================= 输入 =================
	//
	// 和其他三个游戏一样用 _Input，不用 _UnhandledInput：牌桌画在 Node2D 上、不是 Control，
	// _UnhandledInput 会被铺满全屏的根 Control 截胡，表现就是「点哪儿都没反应」。
	// 点按钮时也会顺带进来一次，靠 OverUi + _phase 拦掉。

	private static bool IsEmulated(InputEvent e) => e.Device == -1;

	public override void _Input(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventKey key when key.Pressed && !key.Echo &&
										 (key.Keycode == Key.Escape || key.Keycode == Key.H):
				// 手机上没地方按 H，所以按住牌不动也能看提示（见 TickFrame）
				if (_phase == Phase.Play)
					ShowHint();
				GetViewport().SetInputAsHandled();
				break;

			case InputEventKey keyZ when keyZ.Pressed && !keyZ.Echo && keyZ.Keycode == Key.Z:
				if (_phase == Phase.Play)
					Undo();
				GetViewport().SetInputAsHandled();
				break;

			case InputEventMouseMotion mm when !IsEmulated(mm):
				UpdatePointer(mm.Position);
				break;

			case InputEventScreenDrag sd when !IsEmulated(sd):
				UpdatePointer(sd.Position);
				break;

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
		}
	}

	private void BeginPress(Vector2 pos)
	{
		if (_phase != Phase.Play || _dealing || OverUi(pos))
			return;

		var (c, i) = CardAt(pos);
		if (c < 0)
			return;

		int id = _cols[c][i];
		if (!_up[id])
		{
			Toast("这张还盖着，先把压在上面的牌挪走");
			return;
		}
		if (!IsRunStart(c, i))
		{
			Toast("只能整段搬「同花色、点数连着」的牌");
			return;
		}

		_drag = new Drag
		{
			Col = c,
			Idx = i,
			GrabOffset = pos - CardPos(c, i),
			Pointer = pos,
		};
		_hintLeft = 0f;
		_longPress = 0f;
		Redraw();
	}

	private void UpdatePointer(Vector2 pos)
	{
		var (hc, hi) = CardAt(pos);
		if (hc != _hoverCol || hi != _hoverIdx)
		{
			_hoverCol = hc;
			_hoverIdx = hi;
			if (_drag == null)
				Redraw();
		}

		if (_drag == null)
			return;
		_drag.Pointer = pos;
		if (!_drag.Moved && pos.DistanceTo(CardPos(_drag.Col, _drag.Idx) + _drag.GrabOffset) > TapPx)
			_drag.Moved = true;
		Redraw();
	}

	private void EndPress(Vector2 pos)
	{
		if (_drag == null)
			return;
		var drag = _drag;
		_drag = null;

		// 点一下（没拖动）= 自动走到最合适的位置，手机上全靠这个
		if (!drag.Moved)
		{
			int auto = FindBestDestination(drag.Col, drag.Idx);
			if (auto < 0)
			{
				Toast("这张牌眼下没地方放");
				Redraw();
				return;
			}
			TryMove(drag.Col, drag.Idx, auto);
			return;
		}

		if (pos.Y < BoardTop - 24f || OverUi(pos))
		{
			Redraw();
			return;
		}
		int target = ColumnAtX(pos.X);
		if (target == drag.Col || !TryMove(drag.Col, drag.Idx, target))
		{
			if (target != drag.Col)
				Toast("那儿放不下：只能接在点数大 1 的牌上，或者放进空列");
			Redraw();
		}
	}

	/// <summary>这一点是不是压在按钮/面板上了（那些交给 GUI，牌桌不掺和）。</summary>
	private bool OverUi(Vector2 pos)
	{
		if (_phase != Phase.Play)
			return true;
		foreach (var b in new[] { _homeButton, _setupButton, _restartButton, _undoButton, _hintButton, _dealButton })
			if (b != null && b.Visible && b.GetGlobalRect().HasPoint(pos))
				return true;
		return false;
	}

	private void ShowHint()
	{
		var h = FindHint();
		if (h == null)
		{
			Toast(_stock.Count > 0 ? "牌面上没得走了，发一轮牌试试" : "真的没得走了");
			_hintLeft = 0f;
			Redraw();
			return;
		}
		_hintCol = h.Value.Col;
		_hintIdx = h.Value.Idx;
		_hintDst = h.Value.Dst;
		_hintLeft = HintSeconds;
		Redraw();
	}

	// ================= 每帧 =================

	private void TickFrame(float delta)
	{
		_time += delta;
		bool dirty = false;

		// 发牌 / 收组的飞行
		if (_air.Count > 0)
		{
			var done = new List<int>();
			foreach (var a in _air.Values)
			{
				a.T += delta;
				if (a.T >= a.Dur)
					done.Add(a.Id);
			}
			foreach (int id in done)
				_air.Remove(id);
			dirty = true;
			if (_dealing && _air.Count == 0)
			{
				_dealing = false;
				RecomputeOffsets();
			}
		}

		if (_hintLeft > 0f)
		{
			_hintLeft -= delta;
			dirty = true;
			if (_hintLeft <= 0f)
				_hintCol = _hintIdx = _hintDst = -1;
		}

		if (_toastLeft > 0f)
		{
			_toastLeft -= delta;
			if (_toastLeft <= 0f)
			{
				_toast = "";
				UpdateHintText();
			}
		}

		// 按住牌不动 = 看提示（手机上唯一的提示入口）
		if (_drag != null && !_drag.Moved)
		{
			_longPress += delta;
			if (_longPress >= LongPressSeconds)
			{
				ShowHint();
				_drag = null;
				dirty = true;
			}
		}
		else if (_drag == null)
		{
			_longPress = 0f;
		}

		if (dirty)
			Redraw();
	}

	// ================= 绘制 =================

	/// <summary>
	/// 牌桌。顺序：空列底槽 → 逐列画牌（被压着的只画露出来的那一条）→ 成组区 →
	/// 正在飞的牌 → 最后画被拖着的那一段（永远在最上层）。
	/// </summary>
	private void DrawTable(CanvasItem ci)
	{
		if (_cw <= 0f)
			return;

		for (int c = 0; c < Cols; c++)
			if (_cols[c].Count == 0)
				ci.DrawStyleBox(_sbSlot, new Rect2(PadX + c * (_cw + Gap), BoardTop, _cw, _ch));

		int dragCol = _drag?.Col ?? -1;
		for (int c = 0; c < Cols; c++)
		{
			var list = _cols[c];
			for (int i = 0; i < list.Count; i++)
			{
				int id = list[i];
				if (_air.ContainsKey(id))
					continue;                       // 正在飞，等会儿单独画
				if (c == dragCol && i >= _drag!.Idx)
					continue;                       // 正被拖着
				var pos = CardPos(c, i);
				bool last = i == list.Count - 1;
				int glow = 0;
				if (_hoverCol == c && _hoverIdx == i && _drag == null && _phase == Phase.Play)
					glow = 1;
				DrawCard(ci, id, new Rect2(pos, new Vector2(_cw, VisibleH(c, i))), _up[id], last, glow);
			}
		}

		DrawFoundation(ci);

		foreach (var a in _air.Values)
			DrawAirCard(ci, a);

		if (_drag != null)
			DrawDragRun(ci);

		if (_hintLeft > 0f)
			DrawHint(ci);
	}

	/// <summary>glow：0 无 / 1 悬停或提示 / 2 能放 / 3 放不下。</summary>
	private void DrawCard(CanvasItem ci, int id, Rect2 rect, bool faceUp, bool full, int glow = 0)
	{
		ci.DrawStyleBox(faceUp ? (full ? _sbFace : _sbFaceStrip) : (full ? _sbBack : _sbBackStrip), rect);
		if (glow == 2 || glow == 1)
			ci.DrawStyleBox(_sbGlowGood, rect);
		else if (glow == 3)
			ci.DrawStyleBox(_sbGlowBad, rect);
		if (!faceUp || rect.Size.Y < 26f)
			return;                                  // 露出来的太少，写字反而糊成一团

		var ink = SuitInk(_suit[id]);
		int rankSize = Mathf.Max(13, (int)(_cw * 0.42f));
		ci.DrawString(_font, new Vector2(rect.Position.X + _cw * 0.09f, rect.Position.Y + _ch * 0.29f),
			RankLabel(_rank[id]), HorizontalAlignment.Left, _cw, rankSize, ink);
		if (full)
			GameArt.DrawSuit(ci, _suit[id],
				new Vector2(rect.Position.X + _cw * 0.5f, rect.Position.Y + _ch * 0.66f), _cw * 0.30f, ink);
	}

	private static Color SuitInk(int suit)
		=> suit == SuitHeart || suit == SuitDiamond ? new Color("#d4324a") : new Color("#232b3d");

	private void DrawFoundation(CanvasItem ci)
	{
		for (int i = 0; i < SetsToWin; i++)
		{
			var r = FoundationSlotRect(i);
			if (i < _foundation.Count)
			{
				ci.DrawStyleBox(_sbFace, r);
				GameArt.DrawSuit(ci, _foundation[i], r.Position + r.Size * 0.5f, r.Size.X * 0.30f,
					SuitInk(_foundation[i]));
			}
			else
			{
				ci.DrawStyleBox(_sbSlot, r);
			}
		}
	}

	private static Rect2 FoundationSlotRect(int i)
	{
		float w = FoundationW, h = FoundationH, gap = 6f;
		float total = SetsToWin * w + (SetsToWin - 1) * gap;
		float x0 = (720f - total) * 0.5f;
		return new Rect2(x0 + i * (w + gap), FoundationY, w, h);
	}

	private static Vector2 FoundationSlotCenter(int i) => FoundationSlotRect(i).GetCenter();

	private void DrawAirCard(CanvasItem ci, Air a)
	{
		if (a.T < 0f)
			return;                                  // 还没出发
		float u = Mathf.Clamp(a.T / a.Dur, 0f, 1f);
		float e = 1f - Mathf.Pow(1f - u, 3f);        // easeOutCubic
		var p = a.From.Lerp(a.To, e);
		p.Y -= Mathf.Sin(u * Mathf.Pi) * a.Arc;
		float s = Mathf.Lerp(1f, a.ShrinkTo, e);
		var size = new Vector2(_cw * s, _ch * s);
		var rect = new Rect2(p - size * 0.5f, size);
		bool showFace = a.FaceUp && s > 0.62f;
		ci.DrawStyleBox(a.FaceUp ? (showFace ? _sbFace : _sbFaceStrip) : _sbBack, rect);
		if (showFace)
			GameArt.DrawSuit(ci, _suit[a.Id], rect.Position + rect.Size * 0.5f, size.X * 0.30f, SuitInk(_suit[a.Id]));
	}

	/// <summary>被拖着的那一段：抬起来 + 投影，落点不合法就整体描红。</summary>
	private void DrawDragRun(CanvasItem ci)
	{
		var drag = _drag!;
		var list = _cols[drag.Col];
		int target = drag.Moved ? ColumnAtX(drag.Pointer.X) : -1;
		int glow = 0;
		if (drag.Moved)
			glow = target != drag.Col && CanPlace(list[drag.Idx], target) ? 2 : 3;

		var origin = drag.Pointer - drag.GrabOffset + (drag.Moved ? new Vector2(0f, -6f) : Vector2.Zero);
		float baseY = BoardTop + _ys[drag.Col][drag.Idx];
		for (int i = drag.Idx; i < list.Count; i++)
		{
			var pos = origin + new Vector2(0f, BoardTop + _ys[drag.Col][i] - baseY);
			var rect = new Rect2(pos, new Vector2(_cw, _ch));
			if (i == drag.Idx)
				ci.DrawStyleBox(_sbShadow, new Rect2(pos + new Vector2(4f, 7f), rect.Size));
			DrawCard(ci, list[i], rect, true, true, glow);
		}

		if (drag.Moved && target != drag.Col)
			ci.DrawStyleBox(glow == 2 ? _sbGlowGood : _sbGlowBad,
				new Rect2(PadX + target * (_cw + Gap) - 3f, BoardTop - 3f, _cw + 6f, _ch + 6f));
	}

	private void DrawHint(CanvasItem ci)
	{
		if (_hintCol >= 0 && _hintIdx >= 0 && _hintIdx < _cols[_hintCol].Count)
		{
			var r = new Rect2(CardPos(_hintCol, _hintIdx) - new Vector2(2f, 2f), new Vector2(_cw + 4f, _ch + 4f));
			ci.DrawStyleBox(_sbGlowGood, r);
		}
		if (_hintDst >= 0 && _cols[_hintDst].Count > 0)
		{
			int last = _cols[_hintDst].Count - 1;
			var r = new Rect2(CardPos(_hintDst, last) - new Vector2(2f, 2f), new Vector2(_cw + 4f, _ch + 4f));
			ci.DrawStyleBox(_sbGlowGood, r);
		}
	}

	private static string RankLabel(int rank) => rank switch
	{
		1 => "A",
		11 => "J",
		12 => "Q",
		13 => "K",
		_ => rank.ToString(),
	};

	// ================= 界面 =================

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
		_setupButton.Text = "换难度";
		_setupButton.Pressed += ShowSetup;

		GameArt.StyleButton(_restartButton, new Color("#ff8f6b"), Colors.White, fontSize: 32);
		_restartButton.CustomMinimumSize = new Vector2(140, 80);
		_restartButton.Text = "重开";
		_restartButton.Pressed += () =>
		{
			if (_phase == Phase.Setup && _cols[0].Count == 0)
				ShowSetup();
			else
				StartGame(_levelIndex);
		};
	}

	private void BuildHud()
	{
		_hud = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
		_hud.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_hud.OffsetLeft = 24;
		_hud.OffsetTop = 104;
		_hud.OffsetRight = -24;
		_hud.OffsetBottom = 144;
		_hud.AddThemeConstantOverride("separation", 12);
		_ui.AddChild(_hud);

		_scoreLabel = MakeHudLabel(HorizontalAlignment.Left, new Color("#ffd77a"));
		_midLabel = MakeHudLabel(HorizontalAlignment.Center, Colors.White);
		_setsLabel = MakeHudLabel(HorizontalAlignment.Right, new Color("#7ee787"));
	}

	private Label MakeHudLabel(HorizontalAlignment align, Color color)
	{
		var lb = new Label
		{
			HorizontalAlignment = align,
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		GameArt.OutlineText(lb, color, 26, 6);
		_hud.AddChild(lb);
		return lb;
	}

	private void BuildBottomBar()
	{
		_undoButton = new Button();
		_hintButton = new Button();
		_dealButton = new Button();
		var row = new HBoxContainer();
		row.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		row.OffsetLeft = 20;
		row.OffsetRight = -20;
		row.OffsetTop = -190;
		row.OffsetBottom = -110;
		row.AddThemeConstantOverride("separation", 16);
		_ui.AddChild(row);

		StyleBottomButton(_undoButton, "撤销", new Color("#8f7bff"), 150);
		_undoButton.Pressed += () => Undo();
		row.AddChild(_undoButton);

		StyleBottomButton(_hintButton, "提示", new Color("#40c9a2"), 150);
		_hintButton.Pressed += ShowHint;
		row.AddChild(_hintButton);

		StyleBottomButton(_dealButton, "发牌 (50)", new Color("#ff8f6b"), 0);
		_dealButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_dealButton.Pressed += () => DealStock();
		row.AddChild(_dealButton);

		_hintLabel = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_hintLabel.AddThemeFontSizeOverride("font_size", 22);
		_hintLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_hintLabel.GrowHorizontal = GrowDirection.Both;
		_hintLabel.GrowVertical = GrowDirection.Begin;
		_hintLabel.OffsetTop = -100;
		_hintLabel.OffsetBottom = -58;
		_ui.AddChild(_hintLabel);
	}

	private static void StyleBottomButton(Button b, string text, Color bg, float width)
	{
		b.Text = text;
		if (width > 0f)
			b.CustomMinimumSize = new Vector2(width, 80);
		else
			b.CustomMinimumSize = new Vector2(0, 80);
		GameArt.StyleButton(b, bg, Colors.White, radius: 22, fontSize: 30);
		b.AddThemeStyleboxOverride("disabled", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.08f), 22));
		b.AddThemeColorOverride("font_disabled_color", new Color(1f, 1f, 1f, 0.4f));
	}

	/// <summary>难度面板。和扫雷一个套路：一层半透明遮罩 + 中间的竖排按钮。</summary>
	private void BuildSetup()
	{
		_setup = new Control { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
		_setup.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_setup);

		var dim = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.68f),
			MouseFilter = MouseFilterEnum.Stop,   // 挡住底下的牌桌，选难度时不能接着玩
		};
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_setup.AddChild(dim);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.06f, 0.11f, 0.14f, 0.98f), 34, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 36;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		dim.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 16);
		panel.AddChild(col);

		var title = new Label { Text = "蜘 蛛 纸 牌", HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(title, Colors.White, 54, 8);
		col.AddChild(title);

		var sub = new Label
		{
			Text = "把同花色的 K 一路排到 A，收满 8 组",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		sub.AddThemeFontSizeOverride("font_size", 22);
		sub.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.68f));
		col.AddChild(sub);

		for (int i = 0; i < Levels.Length; i++)
		{
			int idx = i;
			var b = new Button { CustomMinimumSize = new Vector2(560, 108) };
			b.Text = ""; // 内容全交给子节点排，按钮自己不画字
			GameArt.StyleButton(b, new Color(1f, 1f, 1f, 0.09f), Colors.White, radius: 24,
				border: new Color(1f, 1f, 1f, 0.26f));
			b.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.18f), 24, new Color(1f, 1f, 1f, 0.5f)));
			b.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.06f), 24, new Color(1f, 1f, 1f, 0.4f)));
			b.AddThemeStyleboxOverride("focus", GameArt.MakeBox(new Color(0f, 0f, 0f, 0f), 24));
			b.Pressed += () =>
			{
				GD.Print($"[Spider] difficulty -> {Levels[idx].Name}");
				StartGame(idx);
			};
			col.AddChild(b);
			_levelButtons[i] = b;

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

			var name = new Label { Text = Levels[i].Name, MouseFilter = MouseFilterEnum.Ignore };
			GameArt.OutlineText(name, Levels[i].Tint, 38, 6);
			cellCol.AddChild(name);

			var info = new Label { MouseFilter = MouseFilterEnum.Ignore };
			info.AddThemeFontSizeOverride("font_size", 20);
			info.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.72f));
			cellCol.AddChild(info);
			_levelSubs[i] = info;

			var best = new Label
			{
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Center,
				MouseFilter = MouseFilterEnum.Ignore,
			};
			best.AddThemeFontSizeOverride("font_size", 22);
			best.AddThemeColorOverride("font_color", new Color("#ffd77a"));
			row.AddChild(best);
			_levelBests[i] = best;
		}

		_resumeButton = new Button { Text = "继续这一局" };
		_resumeButton.CustomMinimumSize = new Vector2(560, 84);
		GameArt.StyleButton(_resumeButton, new Color("#7ee787"), new Color("#0d2417"), fontSize: 30);
		_resumeButton.Pressed += ResumeGame;
		col.AddChild(_resumeButton);

		var quit = new Button { Text = "返回首页" };
		quit.CustomMinimumSize = new Vector2(560, 84);
		GameArt.StyleButton(quit, new Color("#8f7bff"), Colors.White, fontSize: 30);
		quit.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		col.AddChild(quit);
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
		var box = GameArt.MakeBox(new Color(0.06f, 0.11f, 0.14f, 0.98f), 32, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 40;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		_result.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 14);
		panel.AddChild(col);

		_resultTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(_resultTitle, Colors.White, 52, 8);
		col.AddChild(_resultTitle);

		_resultInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_resultInfo.AddThemeFontSizeOverride("font_size", 28);
		_resultInfo.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(_resultInfo);

		_resultExtra = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_resultExtra.AddThemeFontSizeOverride("font_size", 22);
		_resultExtra.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.7f));
		col.AddChild(_resultExtra);

		_againButton = new Button { Text = "再来一局" };
		_againButton.CustomMinimumSize = new Vector2(520, 84);
		GameArt.StyleButton(_againButton, new Color("#7ee787"), new Color("#0d2417"), fontSize: 30);
		_againButton.Pressed += () => StartGame(_levelIndex);
		col.AddChild(_againButton);

		_resultSetupButton = new Button { Text = "换个难度" };
		_resultSetupButton.CustomMinimumSize = new Vector2(520, 84);
		GameArt.StyleButton(_resultSetupButton, new Color("#4fa8ff"), Colors.White, fontSize: 30);
		_resultSetupButton.Pressed += ShowSetup;
		col.AddChild(_resultSetupButton);

		var quit = new Button { Text = "返回首页" };
		quit.CustomMinimumSize = new Vector2(520, 84);
		GameArt.StyleButton(quit, new Color("#8f7bff"), Colors.White, fontSize: 30);
		quit.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		col.AddChild(quit);
	}

	private void ShowSetup()
	{
		_resumable = _phase == Phase.Play && _cols[0].Count > 0;
		_phase = Phase.Setup;
		_drag = null;
		_result.Visible = false;
		RefreshSetup();
		_setup.Visible = true;
		UpdateHintText();
		Redraw();
	}

	private bool _resumable;

	private void ResumeGame()
	{
		if (!_resumable)
			return;
		_setup.Visible = false;
		_phase = Phase.Play;
		UpdateHintText();
		Redraw();
	}

	private void RefreshSetup()
	{
		for (int i = 0; i < Levels.Length; i++)
		{
			_levelSubs[i].Text = Levels[i].Detail;
			_levelBests[i].Text = _best[i] > 0 ? $"最高分\n{_best[i]}" : "最高分\n—";
		}
		_resumeButton.Visible = _resumable;
	}

	private void ShowResult(string title, string info, string extra)
	{
		_resultTitle.Text = title;
		_resultInfo.Text = info;
		_resultExtra.Text = extra;
		_result.Visible = true;
		UpdateHintText();
	}

	private void UpdateHud()
	{
		if (_score != _vScore)
		{
			_vScore = _score;
			_scoreLabel.Text = $"分数 {_score}";
		}
		if (_moves != _vMoves || _levelIndex != _vMoves2)
		{
			_vMoves = _moves;
			_vMoves2 = _levelIndex;
			_midLabel.Text = $"{Levels[_levelIndex].Name} · {_moves} 步";
		}
		if (_foundation.Count != _vSets)
		{
			_vSets = _foundation.Count;
			_setsLabel.Text = $"成组 {_vSets}/{SetsToWin}";
		}
		if (_stock.Count != _vStock)
		{
			_vStock = _stock.Count;
			_dealButton.Text = $"发牌 ({_stock.Count})";
			_dealButton.Disabled = _stock.Count == 0;
		}
	}

	private int _vMoves2 = int.MinValue;

	private void UpdateHintText()
	{
		if (_hintLabel == null)
			return;
		if (_toastLeft > 0f && _toast.Length > 0)
		{
			_hintLabel.Text = _toast;
			_hintLabel.AddThemeColorOverride("font_color", new Color("#ffd166"));
			return;
		}
		_hintLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.5f));
		_hintLabel.Text = _phase switch
		{
			Phase.Play => "点牌自动走 · 拖牌换位置 · 按住牌看提示",
			_ => "选个难度开始吧：目标是把同花色的 K 排到 A",
		};
	}

	private void Toast(string text)
	{
		_toast = text;
		_toastLeft = ToastSeconds;
		UpdateHintText();
	}

	// ================= 存档 =================

	private void LoadBest()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(BestPath) != Error.Ok)
			return;
		for (int i = 0; i < _best.Length; i++)
			_best[i] = (int)cfg.GetValue("spider", $"best{i}", 0);
	}

	private void SaveBest()
	{
		var cfg = new ConfigFile();
		for (int i = 0; i < _best.Length; i++)
			cfg.SetValue("spider", $"best{i}", _best[i]);
		cfg.Save(BestPath);
	}

	private string DescribeBest()
	{
		var parts = new List<string>();
		for (int i = 0; i < Levels.Length; i++)
			parts.Add($"{Levels[i].Name}={_best[i]}");
		return string.Join(" ", parts);
	}

	// ================= 自测 =================
	//
	// 重点：除了「直接调处理函数」以外，**必须**有几条走真实事件链的断言。
	// 扫雷那次就是栽在这里 —— 21 项断言全过，但真实点击一个都没进来。

	/// <summary>
	/// fire-and-forget 协程里的异常默认被 Task 吞掉 —— 表现就是进程挂着不退出、
	/// 日志戛然而止（这次的越界就是这么哑掉的）。这里兜住、打出栈、强制退出。
	/// </summary>
	private async Task RunSelfTestAsync()
	{
		try
		{
			await SelfTestBodyAsync();
		}
		catch (System.Exception e)
		{
			GD.Print($"[SELFTEST] CRASHED: {e}");
			GD.Print("[SELFTEST] FAILED (自测异常中断)");
			await Wait(0.2);
			GetTree().Quit();
		}
	}

	private async Task SelfTestBodyAsync()
	{
		GD.Print("[SELFTEST] begin (spider)");
		await Wait(0.3);
		int fails = 0;
		var keepBest = (int[])_best.Clone();   // 玩家存档先留着，自测别把假成绩写进去

		// ① 三档难度的牌堆组成：简单全黑桃、普通 黑桃+红桃、困难 四种花色各两套
		for (int lv = 0; lv < Levels.Length; lv++)
		{
			StartGame(lv, animate: false, seed: SelfTestSeed);
			var counts = new int[4];
			for (int id = 0; id < Total; id++)
				counts[_suit[id]]++;
			int suitCount = Levels[lv].SuitCount;
			int perSuit = 13 * (8 / suitCount);
			bool ok = true;
			for (int s = 0; s < 4; s++)
				ok &= counts[s] == (s < suitCount ? perSuit : 0);
			GD.Print($"[SELFTEST] deck \"{Levels[lv].Name}\": 各花色张数 [♠{counts[0]} ♥{counts[1]} ♣{counts[2]} ♦{counts[3]}] " +
					 $"期望每色 {perSuit} × {suitCount} 色 -> {ok}");
			if (!ok) fails++;
		}

		// ② 开局牌型：10 列、前 4 列 6 张其余 5 张、每列一张明牌、牌库 50 张（正好 5 次发牌）
		StartGame(1, animate: false, seed: SelfTestSeed);
		{
			int upCount = 0;
			for (int id = 0; id < Total; id++)
				if (_up[id])
					upCount++;
			bool shape = true;
			var sizes = new List<string>();
			for (int c = 0; c < Cols; c++)
			{
				sizes.Add(_cols[c].Count.ToString());
				shape &= _cols[c].Count == (c < 4 ? 6 : 5);
			}
			bool ok = shape && upCount == Cols && _stock.Count == 50 && _stock.Count == DealSize * MaxStockDeals;
			GD.Print($"[SELFTEST] deal shape: 各列 [{(string.Join(",", sizes))}] 明牌={upCount} " +
					 $"牌库={_stock.Count}（{_stock.Count / DealSize} 次） -> {ok}");
			if (!ok) fails++;
		}

		// ③ 排版：每张牌都在牌桌可用区里，牌也不能小到点不中
		{
			bool fit = _cw >= 44f && _ch >= 64f;
			float minY = float.MaxValue, maxY = float.MinValue;
			for (int c = 0; c < Cols; c++)
				for (int i = 0; i < _cols[c].Count; i++)
				{
					var r = CardRect(c, i);
					minY = Mathf.Min(minY, r.Position.Y);
					maxY = Mathf.Max(maxY, r.End.Y);
					fit &= r.Position.X >= -0.5f && r.End.X <= StageW + 0.5f;
				}
			fit &= minY >= BoardTop - 0.5f && maxY <= BoardBottom + 0.5f;
			GD.Print($"[SELFTEST] layout: 牌 {_cw}×{_ch} 纵向 {minY:0.#}~{maxY:0.#} " +
					 $"(可用 {BoardTop}~{BoardBottom}) -> {fit}");
			if (!fit) fails++;
		}

		// ④ 背景真的铺满了吗（露清屏色就说明背景没覆盖）
		{
			var shot = GetViewport().GetTexture().GetImage();
			var px = new Vector2I((int)(shot.GetWidth() * 0.05f), (int)(shot.GetHeight() * 0.55f));
			var col = shot.GetPixel(px.X, px.Y);
			bool ok = !GameArt.IsClearColor(col);
			GD.Print($"[SELFTEST] background covers screen: pixel{px}={col} -> {ok}");
			if (!ok) fails++;
		}

		SaveShot("spider_setup");

		// ⑤ 可搬段判定：同花连续能整体搬，中间夹了别的花色就只能从断点往上搬
		StartGame(1, animate: false, seed: SelfTestSeed);
		{
			var used = new List<int>();
			int k = Grab(SuitSpade, 13, used), q = Grab(SuitSpade, 12, used), j = Grab(SuitSpade, 11, used);
			int hq = Grab(SuitHeart, 12, used);
			var cols = EmptyCols();
			cols[0] = new List<int> { k, q, j };
			TestBoard(cols);
			bool pure = IsRunStart(0, 0) && IsRunStart(0, 1) && IsRunStart(0, 2);

			cols[0] = new List<int> { k, hq, j };    // 中间夹了一张红桃
			TestBoard(cols);
			bool broken = !IsRunStart(0, 0) && IsRunStart(0, 2);
			GD.Print($"[SELFTEST] 同花连续段可整段搬={pure} 夹花色后只剩末段={broken} -> {pure && broken}");
			if (!(pure && broken)) fails++;
		}

		// ⑥ 走子规则：点数差 1 就能接（花色不限）、差多了不行、空列随便放
		{
			var used = new List<int>();
			int j = Grab(SuitSpade, 11, used), q = Grab(SuitHeart, 12, used), k = Grab(SuitSpade, 13, used);
			var cols = EmptyCols();
			cols[0] = new List<int> { j };
			cols[1] = new List<int> { q };
			cols[2] = new List<int> { k };
			TestBoard(cols);
			bool crossSuit = TryMove(0, 0, 1);       // J♠ 接到 Q♥ 上：点数差 1，花色不同也可以
			bool badRank = !TryMove(1, 0, 2);        // Q 接到 K 上：不是差 1，不许
			bool toEmpty = TryMove(2, 0, 5);         // K 放进空列：可以
			GD.Print($"[SELFTEST] 跨花色接牌={crossSuit} 点数不匹配被拒={badRank} 放进空列={toEmpty} " +
					 $"-> 列1=[{DescribeList(_cols[1])}] 列2=[{DescribeList(_cols[2])}]");
			if (!(crossSuit && badRank && toEmpty)) fails++;
		}

		// ⑥b 目标列变长之后要能立刻用：搬过去的那几张在重算之前就会被收组代码取位置。
		//     这里让搬完正好凑成 K→A —— 只有真能收组时 CollectRuns 才会去读那 13 张的位置，
		//     以前 TryMove 是先 CollectRuns 再 RecomputeOffsets，_ys 还按旧长度分配 → IndexOutOfRange。
		{
			var used = new List<int>();
			var cols = EmptyCols();
			cols[1].Add(Grab(SuitSpade, 1, used));            // 目标列先摆 2 张 → _ys 按最小容量 8 分配
			cols[1].Add(Grab(SuitSpade, 13, used));           // 顶上是 K，能接 Q
			for (int rank = 12; rank >= 1; rank--)            // 源列：Q→A 完整一段（12 张）
				cols[0].Add(Grab(SuitSpade, rank, used));
			TestBoard(cols);
			int before = _cols[1].Count;
			bool moved = TryMove(0, 0, 1);                    // 搬完 14 张，末 13 张 = K→A → 当场收组
			bool collected = _foundation.Count == 1 && _cols[1].Count == 1;
			GD.Print($"[SELFTEST] 目标列变长后立刻收组: 走了={moved} 列1 {before}→{_cols[1].Count} " +
					 $"成组={_foundation.Count} -> {moved && collected}");
			if (!(moved && collected)) fails++;
		}

		// ⑦ 搬走之后，底下露出来的那张牌自动翻开
		{
			var used = new List<int>();
			int seven = Grab(SuitSpade, 7, used), j = Grab(SuitSpade, 11, used), ten = Grab(SuitSpade, 10, used);
			int q = Grab(SuitHeart, 12, used);
			var cols = EmptyCols();
			cols[0] = new List<int> { seven, j, ten };
			cols[1] = new List<int> { q };
			TestBoard(cols);
			_up[seven] = false;                       // 假装它是盖着的
			bool moved = TryMove(0, 1, 1);
			bool flipped = _up[seven] && _cols[0].Count == 1;
			GD.Print($"[SELFTEST] 搬走后自动翻牌: moved={moved} 底牌朝上={flipped} -> {moved && flipped}");
			if (!(moved && flipped)) fails++;
		}

		// ⑧ 发牌：每列各来一张、牌库少 10 张；有空列时不许发；牌库空了也不许发
		{
			var cols = EmptyCols();
			var used = new List<int>();               // used 必须共享：每列各拿一张「不同的」牌，
			for (int c = 0; c < Cols; c++)            // 每列新建一个的话 Grab 会十次拿回同一张
				cols[c] = new List<int> { Grab(SuitSpade, 1 + c, used) };
			TestBoard(cols);
			int stockAtStart = _stock.Count;         // TestBoard 把其余全进牌库 = 104 - 10
			bool dealt = DealStock();
			bool eachOne = true;
			for (int c = 0; c < Cols; c++)
				eachOne &= _cols[c].Count == 2;
			bool tenOff = dealt && _stock.Count == stockAtStart - DealSize;
			GD.Print($"[SELFTEST] 发牌: 每列+1={eachOne} 牌库 {stockAtStart}→{_stock.Count} -> {dealt && eachOne && tenOff}");
			if (!(dealt && eachOne && tenOff)) fails++;

			cols[3] = new List<int>();               // 空出一列
			TestBoard(cols);
			int stockBefore = _stock.Count;
			bool blocked = !DealStock() && _stock.Count == stockBefore;
			GD.Print($"[SELFTEST] 有空列时禁止发牌: {blocked}（牌库仍 {_stock.Count}）");
			if (!blocked) fails++;

			_stock.Clear();
			bool noStock = !DealStock();
			GD.Print($"[SELFTEST] 牌库为空时禁止发牌: {noStock}");
			if (!noStock) fails++;
		}

		// ⑨ 收组：列尾同花色 K→A 自动收走，+100 分，并露出一张新牌
		//    （用「普通」难度：后面 ⑩⑪⑫ 还要抓红桃，全黑桃的牌堆里 Grab 会拿回 -1，
		//     -1 塞进牌列就是这次 _Draw 里 _up[-1] 越界的直接来源）
		StartGame(1, animate: false, seed: SelfTestSeed);
		{
			var used = new List<int>();
			var run = GrabRun(SuitSpade, used);
			int extra = Grab(SuitSpade, 3, used);
			var cols = EmptyCols();
			cols[0] = new List<int>();
			cols[0].Add(extra);
			cols[0].AddRange(run);
			TestBoard(cols);
			_up[extra] = false;
			int scoreBefore = _score;
			int got = CollectRuns();
			bool ok = got == 1 && _foundation.Count == 1 && _score == scoreBefore + PerSetBonus &&
					  _cols[0].Count == 1 && _cols[0][0] == extra && _up[extra];
			GD.Print($"[SELFTEST] 收组: 收了 {got} 组 成组={_foundation.Count} 分数 {scoreBefore}→{_score} " +
					 $"剩下的牌=[{DescribeList(_cols[0])}] 新露出的翻开了={_up[extra]} -> {ok}");
			if (!ok) fails++;
		}

		// ⑩ 撤销：走一步再撤回来，牌面 / 分数 / 步数都要回到原样
		{
			var used = new List<int>();
			int j = Grab(SuitSpade, 11, used), q = Grab(SuitHeart, 12, used);
			var cols = EmptyCols();
			cols[0] = new List<int> { j };
			cols[1] = new List<int> { q };
			TestBoard(cols);
			int steps = _moves, score = _score;
			TryMove(0, 0, 1);
			bool movedOk = _cols[0].Count == 0 && _cols[1].Count == 2 && _moves == steps + 1;
			bool undone = Undo();
			bool backOk = undone && _cols[0].Count == 1 && _cols[0][0] == j && _cols[1].Count == 1 &&
						  _moves == steps && _score == score;
			GD.Print($"[SELFTEST] 撤销: 走完 {movedOk} 撤销后复原={backOk}（步数 {_moves} 分数 {_score}）");
			if (!(movedOk && backOk)) fails++;

			// 发牌也要能撤
			var cols2 = EmptyCols();
			var u2 = new List<int>();
			for (int c = 0; c < Cols; c++)
				cols2[c] = new List<int> { Grab(SuitSpade, 1 + c, u2) };
			TestBoard(cols2);
			int stockBefore = _stock.Count;
			DealStock();
			bool undoDeal = Undo() && _stock.Count == stockBefore && _cols[0].Count == 1;
			GD.Print($"[SELFTEST] 撤销发牌: 牌库回到 {_stock.Count} -> {undoDeal}");
			if (!undoDeal) fails++;
		}

		// ⑪ 提示：有得走时给得出一步，走得动的时候不能瞎报
		{
			var used = new List<int>();
			int j = Grab(SuitSpade, 11, used), q = Grab(SuitHeart, 12, used);
			var cols = EmptyCols();
			cols[0] = new List<int> { j };
			cols[1] = new List<int> { q };
			TestBoard(cols);
			var hint = FindHint();
			var hintCol = hint?.Col ?? -1;
			bool hasHint = hint != null && hintCol == 0 && TryMove(hint!.Value.Col, hint.Value.Idx, hint.Value.Dst);
			GD.Print($"[SELFTEST] 提示给得出可走的步子: {hasHint}");
			if (!hasHint) fails++;
		}

		// ⑫ 卡住判定：10 列各一张全偶数点、牌库空了 → 谁也接不上谁（接上得有奇数点）
		{
			var cols = EmptyCols();
			var used = new List<int>();
			for (int c = 0; c < Cols; c++)
			{
				int rank = 2 + 2 * (c % 5);            // 2/4/6/8/10，两种花色各一张 → 10 张互不相同
				int suit = c < 5 ? SuitSpade : SuitHeart;
				cols[c] = new List<int> { Grab(suit, rank, used) };
			}
			TestBoard(cols);
			_stock.Clear();
			UpdateHud();
			bool noMove = !HasAnyMove();
			CheckEnd();
			bool stuck = noMove && _phase == Phase.Stuck && _result.Visible;
			GD.Print($"[SELFTEST] 卡住判定: 无路可走={noMove} 状态={_phase} 结算面板={_result.Visible} -> {stuck}");
			if (!stuck) fails++;
			_result.Visible = false;
		}

		// ⑬ 通关：7 组已收 + 再收一组 → 8 组满、进结算、最高分落盘
		{
			StartGame(1, animate: false, seed: SelfTestSeed);
			var used = new List<int>();
			var run = GrabRun(SuitSpade, used);
			var cols = EmptyCols();
			cols[0] = new List<int>(run);
			TestBoard(cols);
			for (int i = 0; i < 7; i++)
				_foundation.Add(SuitDiamond);
			_score = 1234;
			CollectRuns();
			CheckEnd();
			int saved = 0;
			var cfg = new ConfigFile();
			if (cfg.Load(BestPath) == Error.Ok)
				saved = (int)cfg.GetValue("spider", "best1", 0);
			bool winOk = _phase == Phase.Win && _result.Visible && _foundation.Count == SetsToWin && saved == 1234 + PerSetBonus;
			GD.Print($"[SELFTEST] 通关: 成组={_foundation.Count}/{SetsToWin} 状态={_phase} " +
					 $"面板={_result.Visible} 最高分落盘={saved} -> {winOk}");
			if (!winOk) fails++;
			SaveShot("spider_win");

			System.Array.Copy(keepBest, _best, _best.Length);   // 假成绩不留档
			SaveBest();
			_result.Visible = false;
		}

		// ⑭ 所有按钮都接上了回调（扫雷那次就是漏了一个 Pressed += ...，按下去毫无反应）
		{
			bool ok = true;
			foreach (var b in new[] { _homeButton, _setupButton, _restartButton, _undoButton, _hintButton, _dealButton,
									  _againButton, _resultSetupButton, _resumeButton })
			{
				bool wired = b.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0;
				if (!wired)
					GD.Print($"[SELFTEST] 按钮 \"{b.Text}\" 没接回调！");
				ok &= wired;
			}
			for (int i = 0; i < Levels.Length; i++)
				ok &= _levelButtons[i].GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0;
			GD.Print($"[SELFTEST] 按钮都接上了回调: {ok}");
			if (!ok) fails++;
		}

		// ⑮ 开局发牌动画：牌先在空中，放完之后全都落在牌位上
		//     （「普通」难度 —— ⑯⑰ 接着要抓红桃，全黑桃的牌堆里 Grab 拿不到）
		{
			StartGame(1, animate: true, seed: SelfTestSeed);
			bool flying = _dealing && _air.Count > 0;
			await Wait(1.8);
			int onTable = 0;
			for (int c = 0; c < Cols; c++)
				onTable += _cols[c].Count;
			bool landed = !_dealing && _air.Count == 0 && onTable == 54;
			GD.Print($"[SELFTEST] 发牌动画: 开始时飞行={flying} 落地后 桌上={onTable} 在飞={_air.Count} -> {flying && landed}");
			if (!(flying && landed)) fails++;
		}

		// ⑯ 真实事件链（一批）：点牌自动走、拖牌落位、发牌/撤销/提示按钮
		{
			var used = new List<int>();
			int j = Grab(SuitSpade, 11, used), q = Grab(SuitHeart, 12, used), k = Grab(SuitSpade, 13, used);
			var cols = EmptyCols();
			cols[0] = new List<int> { j };
			cols[1] = new List<int> { q };
			cols[2] = new List<int> { k };
			TestBoard(cols);

			// ⑯a 点一下最上面那张牌 = 自动走到最合适的位置
			var p0 = LastCardCenter(0);
			PushGuiClick(p0);
			await Wait(0.15);
			bool tapOk = _cols[0].Count == 0 && _cols[1].Count == 2 && _moves == 1;
			GD.Print($"[SELFTEST] gui 点牌自动走: 列0=[{DescribeList(_cols[0])}] 列1=[{DescribeList(_cols[1])}] " +
					 $"步数={_moves} -> {tapOk}");
			if (!tapOk) fails++;

			// ⑯b 拖过去：从列2 的 K 拖到空列 6
			var from = LastCardCenter(2);
			var to = new Vector2(PadX + 6 * (_cw + Gap) + _cw * 0.5f, BoardTop + _ch * 0.5f);
			PushGuiDrag(from, to);
			await Wait(0.15);
			bool dragOk = _cols[2].Count == 0 && _cols[6].Count == 1 && _cols[6][0] == k;
			GD.Print($"[SELFTEST] gui 拖牌落位: 列2=[{DescribeList(_cols[2])}] 列6=[{DescribeList(_cols[6])}] -> {dragOk}");
			if (!dragOk) fails++;

			// ⑯c 花色断开的一段：点「被断点压着的那张」的下缘窄条（它整段搬不动，也不能只搬半段）
			var cols2 = EmptyCols();
			var u2 = new List<int>();
			cols2[0] = new List<int> { Grab(SuitSpade, 9, u2), Grab(SuitHeart, 8, u2) };  // 花色断开
			TestBoard(cols2);
			int snapshot = _moves;
			PushGuiClick(CardPos(0, 0) + new Vector2(_cw * 0.5f, 8f));   // 点在底牌露出的那一条里
			await Wait(0.12);
			bool refused = _moves == snapshot && _cols[0].Count == 2;
			GD.Print($"[SELFTEST] gui 花色断开的段点不动: 步数 {snapshot}→{_moves} -> {refused}");
			if (!refused) fails++;

			// ⑯d 发牌按钮
			var cols3 = EmptyCols();
			var u3 = new List<int>();
			for (int c = 0; c < Cols; c++)
				cols3[c] = new List<int> { Grab(SuitSpade, 1 + c, u3) };
			TestBoard(cols3);
			int stock0 = _stock.Count;
			PushGuiClick(_dealButton.GetGlobalRect().GetCenter());
			await Wait(0.15);
			bool dealOk = _stock.Count == stock0 - DealSize && _cols[0].Count == 2;
			GD.Print($"[SELFTEST] gui 发牌按钮: 牌库 {stock0}→{_stock.Count} -> {dealOk}");
			if (!dealOk) fails++;

			// ⑯e 撤销按钮
			PushGuiClick(_undoButton.GetGlobalRect().GetCenter());
			await Wait(0.15);
			bool undoOk = _stock.Count == stock0 && _cols[0].Count == 1;
			GD.Print($"[SELFTEST] gui 撤销按钮: 牌库回到 {_stock.Count} -> {undoOk}");
			if (!undoOk) fails++;

			// ⑯f 提示按钮
			var cols4 = EmptyCols();
			var u4 = new List<int>();
			int jj = Grab(SuitSpade, 11, u4), qq = Grab(SuitHeart, 12, u4);
			cols4[0] = new List<int> { jj };
			cols4[1] = new List<int> { qq };
			TestBoard(cols4);
			PushGuiClick(_hintButton.GetGlobalRect().GetCenter());
			await Wait(0.15);
			bool hintOk = _hintLeft > 0f && _hintCol == 0 && _hintDst == 1;
			GD.Print($"[SELFTEST] gui 提示按钮: 高亮 列{_hintCol}[{_hintIdx}] → 列{_hintDst} -> {hintOk}");
			if (!hintOk) fails++;
		}

		// ⑰ 系统入口那条路（窗口像素坐标 + Input.parse_input_event）：
		//    这才是真玩家点鼠标时事件进引擎的路，也是唯一会触发引擎「模拟触摸」的路。
		//    一次点击只能走一步 —— 要是 device = -1 的过滤失效，这里会走两步。
		{
			var used = new List<int>();
			int j = Grab(SuitSpade, 11, used), q = Grab(SuitHeart, 12, used);
			var cols = EmptyCols();
			cols[0] = new List<int> { j };
			cols[1] = new List<int> { q };
			TestBoard(cols);
			int steps = _moves;
			var logical = LastCardCenter(0);
			var wpos = WindowPos(logical);
			Input.ParseInputEvent(new InputEventMouseMotion { Position = wpos, GlobalPosition = wpos });
			Input.ParseInputEvent(new InputEventMouseButton
			{
				Position = wpos, GlobalPosition = wpos, ButtonIndex = MouseButton.Left, Pressed = true,
			});
			Input.ParseInputEvent(new InputEventMouseButton
			{
				Position = wpos, GlobalPosition = wpos, ButtonIndex = MouseButton.Left, Pressed = false,
			});
			await Wait(0.25);
			bool osOk = _cols[0].Count == 0 && _cols[1].Count == 2 && _moves == steps + 1;
			GD.Print($"[SELFTEST] os 点牌自动走（窗口坐标 {wpos}）: 列0=[{DescribeList(_cols[0])}] " +
					 $"步数 {steps}→{_moves} -> {osOk}");
			if (!osOk) fails++;
		}

		// ⑱ 收一张中局盘面的截图（这时候牌面已经打散开了）
		{
			StartGame(2, animate: false, seed: SelfTestSeed);
			// 随便走几步同花色接法，让盘面有明有暗
			for (int round = 0; round < 40; round++)
			{
				var hint = FindHint();
				if (hint == null)
					break;
				if (!TryMove(hint.Value.Col, hint.Value.Idx, hint.Value.Dst))
					break;
				if (round % 9 == 8)
					DealStock();
			}
			await Wait(0.35);
			SaveShot("spider_board");
		}

		GD.Print(fails == 0 ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} 项)");
		await Wait(0.4);
		GetTree().Quit();
	}

	// ---------- 自测用的小工具 ----------

	private static List<int>[] EmptyCols()
	{
		var cols = new List<int>[Cols];
		for (int c = 0; c < Cols; c++)
			cols[c] = new List<int>();
		return cols;
	}

	/// <summary>自测用：按花色+点数找一张还没被占用的牌（牌堆要先 BuildDeck 过）。</summary>
	private int Grab(int suit, int rank, List<int> used)
	{
		for (int id = 0; id < Total; id++)
			if (_suit[id] == suit && _rank[id] == rank && !used.Contains(id))
			{
				used.Add(id);
				return id;
			}
		return -1;
	}

	/// <summary>自测用：凑一整套同花色的 K→A（从大到小）。</summary>
	private List<int> GrabRun(int suit, List<int> used)
	{
		var ids = new List<int>();
		for (int rank = 13; rank >= 1; rank--)
		{
			int id = Grab(suit, rank, used);
			if (id < 0)
			{
				GD.Print($"[SELFTEST] 凑不出 {suit} 的 K→A（缺 {rank}）");
				return new List<int>();
			}
			ids.Add(id);
		}
		return ids;
	}

	/// <summary>
	/// 自测用：把牌桌清空重摆 —— 给定列内容（index 0 是列底那张，末尾那张在最上面），
	/// 桌上的牌一律当明牌，其余的牌全进牌库。
	/// </summary>
	private void TestBoard(List<int>[] columns)
	{
		// 防御：摆上桌的牌必须真实存在且不重复。Grab 凑不出会返回 -1，
		// 直接塞进牌列就是 _up[id]/_rank[id] 越界 —— 而且炸在 _Draw 里，
		// 栈信息完全看不出是摆牌摆错了。这里剔除掉、把话说清楚。
		var seen = new HashSet<int>();
		for (int c = 0; c < Cols; c++)
		{
			var clean = new List<int>();
			foreach (int id in columns[c])
			{
				if (id >= 0 && id < Total && seen.Add(id))
					clean.Add(id);
				else
					GD.Print($"[SELFTEST] TestBoard: 列{c} 摆了非法/重复的牌 id={id}，已剔除" +
							 "（多半是 Grab 在当前难度的牌堆里凑不出这张牌）");
			}
			columns[c] = clean;
		}

		var used = new HashSet<int>();
		for (int c = 0; c < Cols; c++)
		{
			_cols[c] = new List<int>(columns[c]);
			foreach (int id in _cols[c])
				used.Add(id);
		}
		_stock.Clear();
		for (int id = 0; id < Total; id++)
			if (!used.Contains(id))
				_stock.Add(id);
		for (int id = 0; id < Total; id++)
			_up[id] = used.Contains(id);

		_air.Clear();
		_drag = null;
		_dealing = false;
		_history.Clear();
		_foundation.Clear();
		_hintCol = _hintIdx = _hintDst = -1;
		_hintLeft = 0f;
		_score = StartScore;
		_moves = 0;
		_phase = Phase.Play;
		_setup.Visible = false;
		_result.Visible = false;
		RecomputeOffsets();
		UpdateHud();
		UpdateHintText();
		Redraw();
	}

	private static string DescribeList(List<int> ids) => ids.Count == 0 ? "空" : string.Join(",", ids);

	/// <summary>推一次「真·点击」给视口：按下 + 抬手，并且连引擎的模拟触摸一起推。</summary>
	private void PushGuiClick(Vector2 pos)
	{
		GetViewport().PushInput(new InputEventMouseButton
		{
			Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = true, Device = 0,
		}, true);
		GetViewport().PushInput(new InputEventMouseButton
		{
			Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = false, Device = 0,
		}, true);
		GetViewport().PushInput(new InputEventScreenTouch { Position = pos, Pressed = true, Device = -1 }, true);
		GetViewport().PushInput(new InputEventScreenTouch { Position = pos, Pressed = false, Device = -1 }, true);
	}

	/// <summary>
	/// 推一次「真·拖动」：按下 → 中间插两次移动（拖拽要超过 TapPx 才算拖）→ 抬手。
	/// 中间那两次移动不能省，否则会被当成「点一下」走自动落位那条分支。
	/// </summary>
	private void PushGuiDrag(Vector2 from, Vector2 to)
	{
		GetViewport().PushInput(new InputEventMouseButton
		{
			Position = from, GlobalPosition = from, ButtonIndex = MouseButton.Left, Pressed = true, Device = 0,
		}, true);
		for (int i = 1; i <= 4; i++)
		{
			var p = from.Lerp(to, i / 4f);
			GetViewport().PushInput(new InputEventMouseMotion { Position = p, GlobalPosition = p, Device = 0 }, true);
		}
		GetViewport().PushInput(new InputEventMouseButton
		{
			Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Left, Pressed = false, Device = 0,
		}, true);
	}

	/// <summary>逻辑坐标（720×1280 的视口坐标系）→ 窗口像素坐标，和操作系统发给游戏的一致。</summary>
	private Vector2 WindowPos(Vector2 logical) => GetViewport().GetFinalTransform() * logical;

	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Spider] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}

	// ================= 内嵌节点 =================

	/// <summary>牌桌。所有绘制都在这一层，逻辑全在 SpiderGame 里。</summary>
	private sealed partial class TableView : Node2D
	{
		private readonly SpiderGame _game;

		public TableView(SpiderGame game) => _game = game;

		public override void _Draw() => _game.DrawTable(this);

		public override void _Process(double delta) => _game.TickFrame((float)delta);
	}
}
