#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 贪吃蛇（首页卡片里的「街机 · 吃豆变长」）。
///
/// 玩法：20×20 的格子上有一条蛇，吃到果子就长一格、加 1 分；撞墙或咬到自己就结束。
/// 每吃一个果子蛇就走得快一点（<see cref="IntervalFor"/>），所以分数越高越刺激。
///
/// 两个设计上的关键点：
/// <list type="bullet">
/// <item>
/// <b>方向不能立刻改，要排队</b>：蛇是按固定节奏一格格走的，玩家可能在两次步进之间
/// 连按两个方向（比如「上」接「左」）。如果每次都直接改当前方向，第一下就会让蛇
/// 原地掉头撞死自己。所以转向先进入 <see cref="_turnQueue"/>，每步只取出一个（见 <see cref="Turn"/>）。
/// </item>
/// <item>
/// <b>尾巴那一格不算撞</b>：不吃果子时尾巴同一帧就会让出来，蛇头挪进去是合法的
/// （贴着尾巴内侧转圈是经典操作），判定里要把这一格排除（见 <see cref="Step"/>）。
/// </item>
/// </list>
///
/// 结构沿用全项目约定：一个脚本 + 一个瘦场景，图形全部 _Draw 现画，不依赖任何素材文件。
/// </summary>
public partial class SnakeGame : Control
{
	// ===================== 可调参数 =====================

	private const int Cols = 20;
	private const int Rows = 20;

	// 设计分辨率（project.godot 里配的 720×1280）。窗口实际像素是另一回事，
	// 自测要把逻辑坐标换算成截图像素时得用它。
	private const float DesignW = 720f;
	private const float DesignH = 1280f;

	private const float PadX = 10f;          // 棋盘左右留白
	private const float BoardTop = 150f;     // 棋盘可用区上边界
	private const float BoardBottom = 930f;  // 棋盘可用区下边界（下面留给方向键）
	private const float MinTile = 16f;
	private const float MaxTile = 40f;

	private const int StartLen = 4;              // 开局蛇长
	private const float BaseInterval = 0.20f;    // 每走一格的秒数（开局）
	private const float MinInterval = 0.075f;    // 最快也不会快过这个
	private const float SpeedUpPerFood = 0.0035f; // 每吃一个提速多少

	private const float PopLife = 0.32f;   // 吃到果子的光圈时长
	private const float ShakeDecay = 30f;  // 撞车震屏的衰减速度（px/s）

	// 方向键面板的几何（Stage 绝对坐标）
	private const float PadUnit = 100f;    // 单个方向键的边长
	private const float PadGap = 8f;       // 键与键之间的缝
	private const float PadTop = 946f;     // 上键的上边

	private const string BestPath = "user://snake_best.cfg";

	// ===================== 状态 =====================

	private enum Phase { Play, Paused, Over }

	private static readonly Vector2I DirUp = new(0, -1);
	private static readonly Vector2I DirDown = new(0, 1);
	private static readonly Vector2I DirLeft = new(-1, 0);
	private static readonly Vector2I DirRight = new(1, 0);

	private readonly List<int> _snake = new();      // 格子下标，**头在最前面**
	private Vector2I _dir = DirRight;               // 当前方向
	private readonly List<Vector2I> _turnQueue = new(); // 待执行的转向（最多 2 个）
	private int _food = -1;                          // 果子所在格子，-1 = 没有

	private Phase _phase = Phase.Play;
	private int _score;
	private float _acc;        // 步进累加器
	private float _interval;   // 当前每格秒数
	private float _shake;
	private double _now;       // 自己累计的时间（自测里好读）

	private int _seed;         // 0 = 按时间随机；自测里固定成一个值好复现
	private System.Random _rng = new();

	private int _best;         // 最高分，0 = 还没玩过

	private sealed class Pop { public Vector2 Pos; public float Age; }
	private readonly List<Pop> _pops = new();

	// 吃果子时的光圈等：颜色常量集中放，画的时候不用每次 new
	private static readonly Color HeadColor = new("#6fe07a");
	private static readonly Color TailColor = new("#2f8f4a");
	private static readonly Color InkColor = new("#12301a");

	// ===================== 子节点 =====================

	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _board = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _restartButton = null!;
	private Button _pauseButton = null!;

	private Button _btnUp = null!;
	private Button _btnDown = null!;
	private Button _btnLeft = null!;
	private Button _btnRight = null!;

	private Label _scoreLabel = null!;
	private Label _hintLabel = null!;

	private ColorRect _overlay = null!;
	private Label _ovTitle = null!;
	private Label _ovInfo = null!;
	private Label _ovLine = null!;
	private Button _ovPrimary = null!;
	private Button _ovSecondary = null!;
	private bool _ovIsPause;

	private GridView _grid = null!;

	// HUD 文本缓存：只在数值真的变了才重建字符串
	private int _hudScore = int.MinValue;

	// 画棋盘用的样式盒（缓存起来，_Draw 里不能每格 new 一个）
	private StyleBoxFlat _sbFrame = null!;
	private StyleBoxFlat _sbFelt = null!;
	private StyleBoxFlat _sbCell = null!;
	private StyleBoxFlat _sbSeg = null!;
	private StyleBoxFlat _sbHead = null!;

	private float _tile = 34f;
	private Vector2 _origin;

	private float StageW => Size.X > 0f ? Size.X : DesignW;

	// ===================== 生命周期 =====================

	public override void _Ready()
	{
		_stage = GetNode<Control>("Stage");
		_background = GetNode<TextureRect>("Stage/Background");
		_board = GetNode<Node2D>("Stage/Board");
		_ui = GetNode<Control>("UI");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_restartButton = GetNode<Button>("UI/TopBar/RestartButton");
		_pauseButton = GetNode<Button>("UI/TopBar/PauseButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		// 根节点铺满全屏，默认的 MouseFilter = Stop 会「认领」所有落在空白处的点击，
		// 把事件从引擎的 GUI 流程里截走。所以根节点必须让路，按钮才收得到点击。
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = GameArt.MakeUiTheme();

		// 背景：深夜绿 → 紫，和蛇的绿身板搭一点
		_background.Texture = GameArt.VerticalGradient(new Color("#0d2a1f"), new Color("#2a1f45"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		BuildStyleBoxes();
		BuildTopBar();
		BuildHud();
		BuildDpad();
		BuildOverlay();
		LoadBest();

		// 棋盘是一块独立画布。注意 QueueRedraw 必须打在**真正重写了 _Draw 的那个节点**上，
		// 打在父节点 Board 上是没用的。
		_grid = new GridView(this);
		_board.AddChild(_grid);

		Layout();
		GetViewport().SizeChanged += Layout;

		GD.Print($"[Snake] ready. best={_best} selftest={SelftestFlag.Describe()}");

		// 先开局：_Process 里只要 phase 是 Play 就会去走一步，而 Step() 假定蛇身非空。
		// 自测开头要 await 一会儿，蛇身空着会当场索引越界，所以自测也必须从「已开局」的状态起跑。
		StartGame();

		if (SelftestFlag.Read() == SelftestFlag.TokenSnake)
			_ = RunSelfTestAsync();
	}

	private void Layout()
	{
		// Control 不会自动铺满父节点：尺寸是 0 的话里面的东西全塌缩成一列看不见的
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」，否则只在左上角画一小块、
		// 其余露出视口清屏色（0.3 灰）。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		ComputeGrid();
	}

	/// <summary>按行列数算格子边长和棋盘原点：取「宽高两个方向都放得下」的较小值，棋盘自然居中。</summary>
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
		LayoutDpad();
	}

	private void BuildStyleBoxes()
	{
		_sbFrame = GameArt.MakeBox(new Color("#1d3b2c"), 22, new Color("#0d1f17"));
		_sbFelt = GameArt.MakeBox(new Color("#123a28"), 14, new Color("#0b2719"));
		_sbCell = GameArt.MakeBox(new Color(1f, 1f, 1f, 0.045f), 6);
		_sbSeg = GameArt.MakeBox(TailColor, 9);
		_sbHead = GameArt.MakeBox(HeadColor, 11);
	}

	// ===================== 一局的开始与结束 =====================

	private void StartGame()
	{
		// 注意别写 Environment.TickCount：这个文件里有 using Godot，
		// 那个名字会解析到 Godot.Environment（3D 环境资源）上去，编译直接报错。
		_rng = new System.Random(_seed != 0 ? _seed : (int)(Time.GetTicksMsec() & 0x7FFFFFFF));

		_score = 0;
		_acc = 0f;
		_shake = 0f;
		_dir = DirRight;
		_turnQueue.Clear();
		_pops.Clear();
		_interval = IntervalFor(0);

		// 蛇身横躺在棋盘正中间偏左，头朝右 —— 右边留出足够空间，开局不会一步撞墙
		_snake.Clear();
		int r0 = Rows / 2;
		int c0 = Cols / 2;
		for (int i = 0; i < StartLen; i++)
			_snake.Add(Idx(c0 - i, r0));

		SpawnFood();

		_phase = Phase.Play;
		_overlay.Visible = false;
		_board.Position = Vector2.Zero;

		_hudScore = int.MinValue;
		UpdateHud();
		Redraw();

		GD.Print($"[Snake] start. 蛇长={_snake.Count} 食物={DescribeCell(_food)} 间隔={_interval:0.###}s");
	}

	/// <summary>每吃一个果子快一点，但不会快过 <see cref="MinInterval"/>。</summary>
	private float IntervalFor(int score) => Mathf.Max(MinInterval, BaseInterval - score * SpeedUpPerFood);

	/// <summary>在空格子里随机挑一个放果子；一个空格都没有说明蛇占满了整盘。</summary>
	private void SpawnFood()
	{
		var free = new List<int>(Rows * Cols);
		for (int i = 0; i < Rows * Cols; i++)
			if (!_snake.Contains(i) && i != _food)
				free.Add(i);

		if (free.Count == 0)
		{
			_food = -1;
			return;
		}
		_food = free[_rng.Next(free.Count)];
	}

	// ===================== 主循环 =====================

	public override void _Process(double delta)
	{
		_now += delta;
		bool anim = false;

		for (int i = _pops.Count - 1; i >= 0; i--)
		{
			_pops[i].Age += (float)delta;
			anim = true;
			if (_pops[i].Age >= PopLife)
				_pops.RemoveAt(i);
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

		if (_phase == Phase.Play)
		{
			_acc += (float)delta;
			// 用 while 而不是 if：卡顿一下攒了两格时间时要把该走的都走掉，
			// 否则蛇会「越走越慢」（累加器一直涨不回来）。
			while (_acc >= _interval)
			{
				_acc -= _interval;
				Step();
				if (_phase != Phase.Play)
					break;
			}
			anim = true;
		}

		if (anim)
			Redraw();
	}

	/// <summary>
	/// 走一格。从 _Process 里剥出来是为了两件事：自测可以直接一步一断言，规则也只有一份。
	/// </summary>
	private void Step()
	{
		if (_phase != Phase.Play)
			return;

		if (_turnQueue.Count > 0)
		{
			_dir = _turnQueue[0];
			_turnQueue.RemoveAt(0);
		}

		int head = _snake[0];
		int nc = ColOf(head) + _dir.X;
		int nr = RowOf(head) + _dir.Y;

		if (nc < 0 || nr < 0 || nc >= Cols || nr >= Rows)
		{
			GameOver("撞到墙了");
			return;
		}

		int next = Idx(nc, nr);
		bool grow = next == _food;

		// 判定范围：不吃果子时尾巴这一格马上就让出来了，蛇头挪进去不算撞。
		int limit = grow ? _snake.Count : _snake.Count - 1;
		for (int i = 0; i < limit; i++)
		{
			if (_snake[i] == next)
			{
				GameOver("咬到自己了");
				return;
			}
		}

		_snake.Insert(0, next);

		if (grow)
		{
			_score++;
			_interval = IntervalFor(_score);
			_pops.Add(new Pop { Pos = CellCenter(nc, nr), Age = 0f });
			SpawnFood();
			UpdateHud();
			SaveBest();
			if (_food < 0)
			{
				// 整盘都是蛇了：这也是「通关」，直接收
				GameOver("把整盘都吃满了！");
				return;
			}
		}
		else
		{
			_snake.RemoveAt(_snake.Count - 1);
		}
	}

	private void GameOver(string reason)
	{
		if (_phase == Phase.Over)
			return;

		_phase = Phase.Over;
		_shake = 12f;
		bool record = _score > _best;
		if (record)
		{
			_best = _score;
			SaveBest();
		}

		ShowOverlay("游戏结束", reason, $"得分 {_score} · 最高 {_best}", false);
		GD.Print($"[Snake] over: {reason} score={_score} best={_best}");
		Redraw();
	}

	// ===================== 操作 =====================

	/// <summary>
	/// 转向。判掉头要拿「队列里最后一个方向」当基准，而不是当前方向：
	/// 蛇往右走时快速按「上」再按「左」，如果跟当前方向比，第二下就会被误拒。
	/// </summary>
	private void Turn(Vector2I d)
	{
		if (_phase != Phase.Play)
			return;

		Vector2I last = _turnQueue.Count > 0 ? _turnQueue[^1] : _dir;
		if (d == last)
			return;
		if (d == new Vector2I(-last.X, -last.Y))
			return; // 180° 掉头 = 直接咬自己的脖子
		if (_turnQueue.Count >= 2)
			return;

		_turnQueue.Add(d);
	}

	private void TogglePause()
	{
		if (_phase == Phase.Play)
		{
			_phase = Phase.Paused;
			ShowOverlay("已暂停", "方向键 / WASD，或屏幕下方方向键", "", true);
		}
		else if (_phase == Phase.Paused)
		{
			Resume();
		}
	}

	private void Resume()
	{
		if (_phase != Phase.Paused)
			return;
		_phase = Phase.Play;
		_acc = 0f; // 别把暂停期间的时间算进来，否则一恢复就冲出去好几格
		_overlay.Visible = false;
	}

	/// <summary>
	/// 键盘走 _Input。棋盘不是 Control，用 _UnhandledInput 会被铺满全屏的根 Control 截掉。
	/// 同时要过滤「模拟事件」（device = -1）：项目开了 mouse→touch，一次按键会收到两遍。
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (@event is not InputEventKey k || !k.Pressed || k.Echo || k.Device == -1)
			return;

		switch (k.Keycode)
		{
			case Key.Up: Turn(DirUp); break;
			case Key.Down: Turn(DirDown); break;
			case Key.Left: Turn(DirLeft); break;
			case Key.Right: Turn(DirRight); break;
			case Key.Space: TogglePause(); break;
			default:
				switch (k.PhysicalKeycode)
				{
					case Key.W: Turn(DirUp); break;
					case Key.S: Turn(DirDown); break;
					case Key.A: Turn(DirLeft); break;
					case Key.D: Turn(DirRight); break;
				}
				break;
		}
	}

	// ===================== 绘制 =====================

	private static int Idx(int c, int r) => r * Cols + c;
	private static int ColOf(int i) => i % Cols;
	private static int RowOf(int i) => i / Cols;

	private Vector2 CellCenter(int c, int r)
		=> new((c + 0.5f) * _tile, (r + 0.5f) * _tile);

	private string DescribeCell(int i)
		=> i < 0 ? "(无)" : $"({ColOf(i)},{RowOf(i)})";

	private void Redraw() => _grid.QueueRedraw();

	private Rect2 CellRect(int c, int r)
		=> new(c * _tile + 1f, r * _tile + 1f, _tile - 2f, _tile - 2f);

	private void DrawBoard(CanvasItem ci)
	{
		float w = _tile * Cols, h = _tile * Rows;

		ci.DrawStyleBox(_sbFrame, new Rect2(-18f, -18f, w + 36f, h + 36f));
		ci.DrawStyleBox(_sbFelt, new Rect2(0f, 0f, w, h));

		// 淡棋盘格：给格子一个参照，不然一格格数不清
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				if (((r + c) & 1) == 0)
					ci.DrawStyleBox(_sbCell, CellRect(c, r));

		// 果子（结束后不再画，画面定格在蛇身上）
		if (_food >= 0)
			DrawApple(ci, CellCenter(ColOf(_food), RowOf(_food)));

		// 蛇：从尾到头画，头压在最上面；颜色从头的亮绿渐变到尾的深绿
		for (int i = _snake.Count - 1; i >= 0; i--)
		{
			int idx = _snake[i];
			var center = CellCenter(ColOf(idx), RowOf(idx));
			float t = _snake.Count <= 1 ? 0f : i / (float)(_snake.Count - 1);
			var col = HeadColor.Lerp(TailColor, t);

			float pad = _tile * (i == 0 ? 0.03f : 0.10f);
			var rect = new Rect2(
				center - Vector2.One * (_tile * 0.5f - pad),
				Vector2.One * (_tile - pad * 2f));

			if (i == 0)
			{
				DrawHead(ci, rect, col);
			}
			else
			{
				_sbSeg.BgColor = col;
				ci.DrawStyleBox(_sbSeg, rect);
			}
		}

		// 吃到果子的金圈
		foreach (var p in _pops)
		{
			float t = Mathf.Clamp(p.Age / PopLife, 0f, 1f);
			float rad = _tile * (0.30f + 0.85f * t);
			ci.DrawArc(p.Pos, rad, 0f, Mathf.Tau, 28,
				new Color(1f, 0.85f, 0.35f, 1f - t), 4f, true);
		}
	}

	private void DrawHead(CanvasItem ci, Rect2 rect, Color col)
	{
		_sbHead.BgColor = col;
		ci.DrawStyleBox(_sbHead, rect);

		// 眼睛长在「朝前」的那一侧；两只眼的位置靠垂直于方向的轴算出来
		var c = rect.GetCenter();
		var fwd = new Vector2(_dir.X, _dir.Y);
		var perp = new Vector2(-_dir.Y, _dir.X);
		float eyeR = Mathf.Max(1.7f, _tile * 0.115f);
		var basePos = c + fwd * (_tile * 0.16f);

		foreach (int sgn in new[] { 1, -1 })
		{
			var eye = basePos + perp * (_tile * 0.20f * sgn);
			ci.DrawCircle(eye, eyeR, Colors.White);
			ci.DrawCircle(eye + fwd * (eyeR * 0.45f), eyeR * 0.55f, InkColor);
		}

		// 吐信子：一小截红线，让「头朝哪边」一眼看得出来
		ci.DrawLine(c + fwd * (_tile * 0.42f), c + fwd * (_tile * 0.62f),
			new Color("#ff6b81"), Mathf.Max(1.6f, _tile * 0.07f), true);
	}

	private void DrawApple(CanvasItem ci, Vector2 c)
	{
		float r = _tile * 0.33f;
		var body = new Color("#ff5c6e");
		ci.DrawCircle(c + new Vector2(0f, r * 0.14f), r, body);
		ci.DrawArc(c + new Vector2(0f, r * 0.14f), r, 0f, Mathf.Tau, 24,
			new Color("#a11f33"), Mathf.Max(1.4f, r * 0.13f), true);
		ci.DrawCircle(c + new Vector2(-r * 0.34f, -r * 0.10f), r * 0.26f, new Color(1f, 1f, 1f, 0.62f));

		// 果柄 + 叶子
		ci.DrawLine(c + new Vector2(0f, -r * 0.85f), c + new Vector2(r * 0.14f, -r * 1.35f),
			new Color("#7a4a22"), Mathf.Max(1.4f, r * 0.15f), true);
		GameArt.Ellipse(ci, c + new Vector2(r * 0.46f, -r * 1.18f), r * 0.36f, r * 0.21f,
			new Color("#6fe07a"), new Color("#2f6b2c"), Mathf.Max(1f, r * 0.10f));
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

		StyleBarButton(_homeButton, "返回", new Color("#8f7bff"));
		_homeButton.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);

		StyleBarButton(_restartButton, "重开", new Color("#ff8f6b"));
		_restartButton.Pressed += StartGame;

		StyleBarButton(_pauseButton, "暂停", new Color("#4fa8ff"));
		_pauseButton.Pressed += TogglePause;
	}

	private static void StyleBarButton(Button b, string text, Color bg)
	{
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 32);
		b.CustomMinimumSize = new Vector2(140, 80);
		b.Text = text;
		// 方向键是键盘操作的核心，按钮抢走焦点后空格/方向键会被 GUI 吃掉，所以一律不要焦点
		b.FocusMode = FocusModeEnum.None;
	}

	private void BuildHud()
	{
		_scoreLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		GameArt.OutlineText(_scoreLabel, new Color("#ffd77a"), 40, 8);
		_scoreLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_scoreLabel.GrowHorizontal = GrowDirection.Both;
		_scoreLabel.OffsetTop = 108;
		_scoreLabel.OffsetBottom = 166;
		_ui.AddChild(_scoreLabel);

		_hintLabel = new Label
		{
			Text = "方向键 / WASD 控制 · 吃到果子变长提速",
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
			_scoreLabel.Text = $"得分 {_score} · 最高 {Mathf.Max(_best, _score)}";
		}
	}

	private void BuildDpad()
	{
		_btnUp = MakePadButton("▲");
		_btnDown = MakePadButton("▼");
		_btnLeft = MakePadButton("◀");
		_btnRight = MakePadButton("▶");

		_btnUp.Pressed += () => Turn(DirUp);
		_btnDown.Pressed += () => Turn(DirDown);
		_btnLeft.Pressed += () => Turn(DirLeft);
		_btnRight.Pressed += () => Turn(DirRight);
	}

	private Button MakePadButton(string text)
	{
		var b = new Button { Text = text, FocusMode = FocusModeEnum.None };
		GameArt.StyleButton(b, new Color(1f, 1f, 1f, 0.14f), Colors.White, radius: 22,
			border: new Color(1f, 1f, 1f, 0.34f), fontSize: 40);
		_ui.AddChild(b);
		return b;
	}

	private void LayoutDpad()
	{
		float cx = StageW * 0.5f;
		float u = PadUnit, g = PadGap;
		float midY = PadTop + u + g;

		PlaceButton(_btnUp, new Vector2(cx - u * 0.5f, PadTop));
		PlaceButton(_btnLeft, new Vector2(cx - u * 1.5f - g, midY));
		PlaceButton(_btnDown, new Vector2(cx - u * 0.5f, midY));
		PlaceButton(_btnRight, new Vector2(cx + u * 0.5f + g, midY));
	}

	/// <summary>按绝对坐标摆一个按钮（锚点钉在左上角，位置尺寸直接写像素）。</summary>
	private static void PlaceButton(Button b, Vector2 pos)
	{
		var size = new Vector2(PadUnit, PadUnit);
		b.AnchorLeft = 0f;
		b.AnchorTop = 0f;
		b.AnchorRight = 0f;
		b.AnchorBottom = 0f;
		b.OffsetLeft = pos.X;
		b.OffsetTop = pos.Y;
		b.OffsetRight = pos.X + size.X;
		b.OffsetBottom = pos.Y + size.Y;
		b.CustomMinimumSize = size;
	}

	private void BuildOverlay()
	{
		_overlay = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.62f),
			MouseFilter = MouseFilterEnum.Stop,
			Visible = false,
		};
		_overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_overlay);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.08f, 0.10f, 0.19f, 0.98f), 32, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 40;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		_overlay.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 16);
		panel.AddChild(col);

		_ovTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(_ovTitle, Colors.White, 54, 8);
		col.AddChild(_ovTitle);

		_ovInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_ovInfo.AddThemeFontSizeOverride("font_size", 30);
		_ovInfo.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.86f));
		col.AddChild(_ovInfo);

		_ovLine = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_ovLine.AddThemeFontSizeOverride("font_size", 30);
		_ovLine.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(_ovLine);

		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 16);
		col.AddChild(row);

		_ovPrimary = MakeOverlayButton("再来一局", new Color("#ff8f6b"));
		_ovPrimary.Pressed += () =>
		{
			if (_ovIsPause)
				Resume();
			else
				StartGame();
		};
		row.AddChild(_ovPrimary);

		_ovSecondary = MakeOverlayButton("回首页", new Color("#8f7bff"));
		_ovSecondary.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		row.AddChild(_ovSecondary);
	}

	private Button MakeOverlayButton(string text, Color bg)
	{
		var b = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(200, 96),
			FocusMode = FocusModeEnum.None,
		};
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 30);
		return b;
	}

	private void ShowOverlay(string title, string info, string line, bool pause)
	{
		_ovTitle.Text = title;
		_ovInfo.Text = info;
		_ovLine.Text = line;
		_ovLine.Visible = line.Length > 0;
		_ovPrimary.Text = pause ? "继续" : "再来一局";
		_ovIsPause = pause;
		_overlay.Visible = true;
	}

	// ===================== 最高分存档 =====================

	private void LoadBest()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(BestPath) != Error.Ok)
			return;
		_best = cfg.GetValue("best", "score", 0).AsInt32();
	}

	private void SaveBest()
	{
		var cfg = new ConfigFile();
		cfg.SetValue("best", "score", _best);
		var err = cfg.Save(BestPath);
		if (err != Error.Ok)
			GD.PushError($"[Snake] save best failed: {err}");
	}

	// ===================== 自测 =====================
	//
	// 触发方式：项目根目录放 selftest.flag，内容写 snake，然后启动游戏
	// （首页会立刻切到本场景，本场景看到内容是自己就跑这一套）。

	private async Task RunSelfTestAsync()
	{
		GD.Print("[SELFTEST] begin (snake)");
		try
		{
			await Wait(0.4);

			int fails = 0;
			_seed = 20260926; // 固定随机，跑出来的盘面每次一样，出问题好复现
			// 自测会刷出好成绩（比如自动吃满盘），真去玩的人再也刷不掉，
			// 所以进门先备份玩家原有的纪录，全部验完再写回去。
			int keepBest = _best;

			// ① 开局：蛇长、都在盘内、方向向右、食物不在蛇身上
			StartGame();
			bool lenOk = _snake.Count == StartLen;
			bool insideOk = true;
			foreach (int i in _snake)
				if (i < 0 || i >= Rows * Cols)
					insideOk = false;
			bool uniqueOk = new HashSet<int>(_snake).Count == _snake.Count;
			bool foodOk = _food >= 0 && !_snake.Contains(_food);
			GD.Print($"[SELFTEST] 开局: 蛇长={_snake.Count}(应 {StartLen}) 都在盘内={insideOk} 不重叠={uniqueOk} " +
					 $"方向={_dir} 食物={DescribeCell(_food)} 不在蛇身={foodOk} -> {lenOk && insideOk && uniqueOk && foodOk}");
			if (!lenOk || !insideOk || !uniqueOk || !foodOk) fails++;

			// ② 普通一步：头往前一格、长度不变
			var headBefore = CellOf(_snake[0]);
			int lenBefore = _snake.Count;
			int scoreBefore = _score;
			_food = -1; // 先挪开果子，保证这一步是「没吃到」的那条路
			Step();
			var headAfter = CellOf(_snake[0]);
			bool moveOk = headAfter == headBefore + _dir && _snake.Count == lenBefore && _score == scoreBefore;
			GD.Print($"[SELFTEST] 走一步: 头 {headBefore}->{headAfter} 长 {lenBefore}->{_snake.Count} " +
					 $"分 {scoreBefore}->{_score} -> {moveOk}");
			if (!moveOk) fails++;

			// ③ 吃到果子：分数 +1、蛇长 +1、新果子不在蛇身上
			StartGame();
			int lenB = _snake.Count;
			_food = InFrontOfHead(); // 把果子摆到正前方，下一步必定吃到
			Step();
			bool eatOk = _score == 1 && _snake.Count == lenB + 1 &&
						 _food >= 0 && !_snake.Contains(_food);
			GD.Print($"[SELFTEST] 吃到果子: 分={_score} 长 {lenB}->{_snake.Count} 新果子={DescribeCell(_food)} -> {eatOk}");
			if (!eatOk) fails++;

			// ④ 提速：吃果子之后间隔必须变短（但不会快过下限）
			float i0 = IntervalFor(0), i5 = IntervalFor(5), iBig = IntervalFor(999);
			bool speedOk = i5 < i0 && iBig >= MinInterval && iBig <= MinInterval + 0.0001f;
			GD.Print($"[SELFTEST] 提速: 0 分={i0:0.###}s 5 分={i5:0.###}s 极高分={iBig:0.###}s(下限 {MinInterval}) -> {speedOk}");
			if (!speedOk) fails++;

			// ⑤ 撞墙：头贴着右边界再走一步 → 结束
			StartGame();
			_snake.Clear();
			_snake.Add(Idx(Cols - 1, 5));
			_snake.Add(Idx(Cols - 2, 5));
			_snake.Add(Idx(Cols - 3, 5));
			_dir = DirRight;
			_turnQueue.Clear();
			_food = -1;
			Step();
			bool wallOk = _phase == Phase.Over && _overlay.Visible;
			GD.Print($"[SELFTEST] 撞墙: phase={_phase} overlay={_overlay.Visible} -> {wallOk}");
			if (!wallOk) fails++;

			// ⑥ 咬到自己：蛇头右侧就是自己的身子 → 结束
			StartGame();
			_snake.Clear();
			_snake.Add(Idx(5, 5));  // 头
			_snake.Add(Idx(6, 5));  // 下一步要撞上来的那节
			_snake.Add(Idx(6, 6));
			_snake.Add(Idx(5, 6));
			_snake.Add(Idx(4, 6));  // 尾巴（不是 (6,5)，所以确实该判撞）
			_dir = DirRight;
			_turnQueue.Clear();
			_food = -1;
			Step();
			bool selfOk = _phase == Phase.Over;
			GD.Print($"[SELFTEST] 咬到自己: phase={_phase} -> {selfOk}");
			if (!selfOk) fails++;

			// ⑦ 贴着尾巴走不算撞：尾巴那一格这一步就会让出来
			StartGame();
			_snake.Clear();
			_snake.Add(Idx(5, 5));  // 头
			_snake.Add(Idx(5, 6));
			_snake.Add(Idx(6, 6));
			_snake.Add(Idx(6, 5));  // 尾巴：正是头下一步要进的那格
			_dir = DirRight;
			_turnQueue.Clear();
			_food = -1;
			Step();
			bool tailOk = _phase == Phase.Play && _snake[0] == Idx(6, 5);
			GD.Print($"[SELFTEST] 贴着尾巴走: phase={_phase} 头={DescribeCell(_snake[0])} -> {tailOk}");
			if (!tailOk) fails++;

			// ⑧ 不许 180° 掉头
			StartGame();
			_dir = DirRight;
			_turnQueue.Clear();
			Turn(DirLeft);
			bool revOk = _turnQueue.Count == 0 && _dir == DirRight;
			GD.Print($"[SELFTEST] 禁止掉头: 队列={_turnQueue.Count} 方向={_dir} -> {revOk}");
			if (!revOk) fails++;

			// ⑨ 转向排队：连按「上」「左」两下都要记下来，不能因为「上」还没执行就丢掉「左」
			Turn(DirUp);
			Turn(DirLeft);
			bool queueOk = _turnQueue.Count == 2;
			Step();
			bool q1 = _dir == DirUp && _turnQueue.Count == 1;
			Step();
			bool q2 = _dir == DirLeft && _turnQueue.Count == 0;
			GD.Print($"[SELFTEST] 转向排队: 入队两个={queueOk} 第一步朝上={q1} 第二步朝左={q2} -> {queueOk && q1 && q2}");
			if (!queueOk || !q1 || !q2) fails++;

			// ⑩ 暂停：暂停后时间不再推进蛇
			StartGame();
			TogglePause();
			bool pausedOk = _phase == Phase.Paused && _overlay.Visible && _ovPrimary.Text == "继续";
			var headP = CellOf(_snake[0]);
			await Wait(0.5);
			bool frozenOk = CellOf(_snake[0]) == headP;
			Resume();
			bool resumedOk = _phase == Phase.Play && !_overlay.Visible;
			GD.Print($"[SELFTEST] 暂停/继续: 暂停={pausedOk} 0.5s 后头不动={frozenOk} 恢复={resumedOk}");
			if (!pausedOk || !frozenOk || !resumedOk) fails++;

			// ⑪ 最高分真的落盘了
			StartGame();
			_best = 0;
			for (int i = 0; i < 3; i++)
			{
				_food = InFrontOfHead();
				Step();
			}
			GameOver("自测");
			var cfg = new ConfigFile();
			int saved = cfg.Load(BestPath) == Error.Ok ? cfg.GetValue("best", "score", -1).AsInt32() : -1;
			bool bestOk = _best == 3 && saved == 3;
			GD.Print($"[SELFTEST] 最高分落盘: mem={_best} file={saved} -> {bestOk}");
			if (!bestOk) fails++;
			SaveShot("snake_over");

			// 还原玩家的纪录（含文件）
			_best = keepBest;
			SaveBest();

			// ⑫ 真实 GUI 链路：点屏幕上的「▲」方向键，方向必须真的转过去
			StartGame();
			_dir = DirRight;
			_turnQueue.Clear();
			PushGuiClick(_btnUp.GetGlobalRect().GetCenter());
			await Wait(0.15);
			bool guiOk = _turnQueue.Count == 1 && _turnQueue[0] == DirUp;
			GD.Print($"[SELFTEST] gui 点「▲」: 队列={_turnQueue.Count} 首个={(_turnQueue.Count > 0 ? _turnQueue[0].ToString() : "-")} -> {guiOk}");
			if (!guiOk) fails++;

			// ⑬ 棋盘算得对：装得进可用区、格子不小于下限、方向键在棋盘下方不重叠
			bool gridOk = _tile >= MinTile && _tile * Cols <= StageW - PadX * 2f + 0.5f &&
						  _origin.Y >= BoardTop - 0.5f && _origin.Y + _tile * Rows <= BoardBottom + 0.5f;
			float boardBottomPx = _origin.Y + _tile * Rows;
			bool padOk = _btnUp.OffsetTop >= boardBottomPx;
			GD.Print($"[SELFTEST] 布局: tile={_tile} origin={_origin} 棋盘下沿={boardBottomPx:0.#} " +
					 $"上键 y={_btnUp.OffsetTop} -> {gridOk && padOk}");
			if (!gridOk || !padOk) fails++;

			// ⑭ 背景真的铺满了吗（露出 0.3 灰的清屏色就说明背景没覆盖整屏）
			var shot = GetViewport().GetTexture().GetImage();
			var px = new Vector2I((int)(shot.GetWidth() * 0.01f), (int)(shot.GetHeight() * 0.5f));
			var col = shot.GetPixel(px.X, px.Y);
			bool bgOk = !GameArt.IsClearColor(col);
			GD.Print($"[SELFTEST] background covers screen: pixel{px}={col} -> {bgOk}");
			if (!bgOk) fails++;

			// ⑭b 蛇真的画出来了吗：把蛇头那一格的像素抓出来数「亮绿」占比。
			//      光验数据/逻辑的断言盯不住「画布根本没重画」这类问题。
			//      两个要点：
			//      a) 先把 _interval 拉得极大，让蛇在等待期间不要自己走掉 ——
			//         否则「逻辑里蛇头的位置」和「上一帧画出来的蛇头」差一格，探针就成了随机结果；
			//      b) 数整块格子而不是取一个点，避开圆角 / 眼睛 / 信子，也不怕 1px 的取整误差。
			StartGame();
			_interval = 1e6f;
			await Wait(0.2);
			var boardShot = GetViewport().GetTexture().GetImage();
			float kx = boardShot.GetWidth() / DesignW;
			float ky = boardShot.GetHeight() / DesignH;
			int hc = ColOf(_snake[0]);
			int hr = RowOf(_snake[0]);
			// 只取格子中间那 60%，四周留给圆角
			float x0 = _origin.X + (hc + 0.2f) * _tile;
			float y0 = _origin.Y + (hr + 0.2f) * _tile;
			int greenPx = 0, totalPx = 0;
			for (float y = y0; y < y0 + _tile * 0.6f; y += 1f)
			{
				for (float x = x0; x < x0 + _tile * 0.6f; x += 1f)
				{
					var pc = boardShot.GetPixel((int)(x * kx), (int)(y * ky));
					totalPx++;
					if (pc.G > 0.55f && pc.G > pc.R * 1.25f && pc.G > pc.B * 1.2f)
						greenPx++;
				}
			}
			float greenRatio = totalPx > 0 ? (float)greenPx / totalPx : 0f;
			bool headVisible = greenRatio > 0.6f;
			GD.Print($"[SELFTEST] 蛇头可见: 格({hc},{hr}) 亮绿像素 {greenPx}/{totalPx} = {greenRatio:0.##} -> {headVisible}");
			if (!headVisible) fails++;

			// ⑮ 正常玩一会儿，截一张中局盘面（自动吃几个果子）
			StartGame();
			for (int i = 0; i < 5; i++)
			{
				_food = InFrontOfHead();
				Step();
			}
			await Wait(0.25);
			SaveShot("snake_board");

			GD.Print(fails == 0 ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} 项)");
			await Wait(0.4);
			GetTree().Quit();
		}
		catch (System.Exception e)
		{
			// 自测是 fire-and-forget 协程，异常会被 Task 静默吞掉（表现为日志戛然而止、进程挂着不退出）
			GD.Print($"[SELFTEST] CRASHED: {e}");
			GetTree().Quit(1);
		}
	}

	// ---------- 自测用的小工具 ----------

	/// <summary>蛇头正前方那一格（撞墙时返回 -1）。</summary>
	private int InFrontOfHead()
	{
		int head = _snake[0];
		int c = ColOf(head) + _dir.X;
		int r = RowOf(head) + _dir.Y;
		if (c < 0 || r < 0 || c >= Cols || r >= Rows)
			return -1;
		return Idx(c, r);
	}

	private static Vector2I CellOf(int idx) => new(ColOf(idx), RowOf(idx));

	/// <summary>把一次点击推给视口，走引擎的 GUI 命中测试（能测出「事件根本没送到按钮」）。</summary>
	private void PushGuiClick(Vector2 pos)
	{
		var vp = GetViewport();
		vp.PushInput(new InputEventMouseButton
		{
			Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left,
			Pressed = true, Device = 0,
		}, true);
		vp.PushInput(new InputEventMouseButton
		{
			Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left,
			Pressed = false, Device = 0,
		}, true);
	}

	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Snake] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}

	// ===================== 内嵌节点 =====================
	//
	// 注意：C# 里凡是继承 Godot 节点类型的类（哪怕是嵌套的私有类）都必须写 partial，
	// 否则编译直接报 GD0001。

	/// <summary>棋盘画布。逻辑都在 SnakeGame 里，它只负责把 _Draw 转发过去。</summary>
	private sealed partial class GridView : Node2D
	{
		private readonly SnakeGame _game;

		public GridView(SnakeGame game)
		{
			_game = game;
		}

		public override void _Draw() => _game.DrawBoard(this);
	}
}
