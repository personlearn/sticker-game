#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 首页（游戏选择界面）——整个项目的主场景。
///
/// 职责很单一：摆出几个「游戏卡片」，点哪张就切到哪个场景。
/// 加一个新游戏的步骤：
/// <list type="number">
/// <item>在 <see cref="ScenePaths"/> 里加一条路径常量；</item>
/// <item>在 <c>scenes/Home.tscn</c> 里加一个 Button 节点（放在 <c>UI/Cards</c> 下）；</item>
/// <item>在 <see cref="BuildCards"/> 里加一条 <see cref="GameEntry"/>。</item>
/// </list>
///
/// 首页同时也负责把 <c>selftest.flag</c> 路由到对应的游戏（见 <see cref="SelftestFlag"/>）。
/// </summary>
public partial class Home : Control
{
	/// <summary>一张游戏卡片的数据。</summary>
	private sealed class GameEntry
	{
		public string Label = "";
		public string Type = "";   // 游戏类型（就是用户说的「要游玩的游戏类型」）
		public string Desc = "";
		public string Scene = "";
		public Button Card = null!;
		public Label DescLabel = null!;
	}

	private enum CardIcon { Doll, Ship, Sheep, Mine, Spider }

	/// <summary>
	/// 卡片高度。卡片从 4 张变 5 张之后，196 高 + 28 间距一共要 1092px，
	/// 而可用区只有 910px —— 会直接顶穿副标题和底部提示，所以整排要收一收。
	/// </summary>
	private const float CardH = 160f;


	private readonly List<GameEntry> _games = new();

	private TextureRect _background = null!;
	private Node2D _decor = null!;
	private Label _title = null!;
	private Label _subtitle = null!;
	private Label _hint = null!;
	private VBoxContainer _cards = null!;

	public override void _Ready()
	{
		_background = GetNode<TextureRect>("Stage/Background");
		_decor = GetNode<Node2D>("Stage/Decor");
		_title = GetNode<Label>("UI/Title");
		_subtitle = GetNode<Label>("UI/Subtitle");
		_hint = GetNode<Label>("UI/Hint");
		_cards = GetNode<VBoxContainer>("UI/Cards");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		Theme = GameArt.MakeUiTheme();

		BuildBackground();
		BuildHeader();
		BuildCards();
		BuildHint();
		Layout();

		GetViewport().SizeChanged += Layout;

		GD.Print($"[Home] ready. selftest = {SelftestFlag.Describe()}");

		// 自测路由：flag 的**内容**决定去哪。
		// 注意「没有这个文件」必须走正常流程——不然删掉开关之后按 F5 反而会直接自测并退出。
		string token = SelftestFlag.Read();
		switch (token)
		{
			case SelftestFlag.TokenSticker:
				RouteToScene(ScenePaths.StickerGame);
				break;
			case SelftestFlag.TokenThunder:
				RouteToScene(ScenePaths.ThunderGame);
				break;
			case SelftestFlag.TokenSheep:
				RouteToScene(ScenePaths.SheepGame);
				break;
			case SelftestFlag.TokenMines:
				RouteToScene(ScenePaths.MinesweeperGame);
				break;
			case SelftestFlag.TokenSpider:
				RouteToScene(ScenePaths.SpiderGame);
				break;
			case SelftestFlag.TokenHome:
				// 首页自己的自测：马上要截图，所以不放入场动画
				_ = RunSelfTestAsync();
				break;
			default:
				// 没有开关文件、或者内容不认识 → 正常进首页
				_ = EnterAnimationAsync();
				break;
		}
	}

	/// <summary>
	/// 从 <see cref="_Ready"/> 里换场景必须推迟到本帧末尾。
	/// 直接调 ChangeSceneToFile 会踩到 Godot 的
	/// 「Parent node is busy adding/removing children, remove_child() can't be called at this time」，
	/// 因为此刻场景树还在把当前场景挂上去。
	/// </summary>
	private void RouteToScene(string scene)
	{
		Callable.From(() =>
		{
			var err = GetTree().ChangeSceneToFile(scene);
			if (err != Error.Ok)
				GD.PushError($"[Home] route to {scene} failed: {err}");
		}).CallDeferred();
	}

	// ================= 建界面 =================

	private void BuildBackground()
	{
		// 竖直渐变用 GradientTexture2D：纯 GPU 侧，不用像贴纸游戏那样逐像素画 720×1280。
		_background.Texture = GameArt.VerticalGradient(new Color("#0c1230"), new Color("#3b2359"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;
		_decor.ZIndex = -10;
		_decor.AddChild(new DecorBackdrop());
	}

	private void BuildHeader()
	{
		_title.Text = "小游戏屋";
		_title.HorizontalAlignment = HorizontalAlignment.Center;
		GameArt.OutlineText(_title, Colors.White, 72, 8);
		_title.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_title.OffsetTop = 92;
		_title.OffsetBottom = 188;

		_subtitle.Text = "选一个开始玩";
		_subtitle.HorizontalAlignment = HorizontalAlignment.Center;
		_subtitle.AddThemeFontSizeOverride("font_size", 30);
		_subtitle.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.66f));
		_subtitle.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_subtitle.OffsetTop = 196;
		_subtitle.OffsetBottom = 244;
	}

	private void BuildCards()
	{
		_cards.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		// 卡片多了之后不能再用「整屏居中」：四张卡叠起来会顶到副标题上。
		// 把卡片的可用区钉在「副标题以下、底部提示以上」，再让 VBox 在这个区间里居中，
		// 这样以后再加游戏也只是往里挤，不会往上撞标题。
		_cards.OffsetTop = 250;
		_cards.OffsetBottom = -120;
		_cards.Alignment = BoxContainer.AlignmentMode.Center;
		// 5 张卡片： 5×160 + 4×18 = 872 ≤ 可用高度 910
		_cards.AddThemeConstantOverride("separation", 18);
		_cards.MouseFilter = MouseFilterEnum.Ignore;

		// 卡片节点本身写在 scenes/Home.tscn 里（顺序就是显示顺序），这里只填内容。
		AddGame(new GameEntry
		{
			Label = "贴纸游戏",
			Type = "装扮 · 换衣服",
			Desc = "换睡衣、摆贴纸、拍照留念",
			Scene = ScenePaths.StickerGame,
		}, GetNode<Button>("UI/Cards/StickerCard"), CardIcon.Doll);

		AddGame(new GameEntry
		{
			Label = "雷霆战机",
			Type = "飞行射击",
			Desc = "躲弹幕、打敌机，一路火力升级",
			Scene = ScenePaths.ThunderGame,
		}, GetNode<Button>("UI/Cards/ThunderCard"), CardIcon.Ship);

		AddGame(new GameEntry
		{
			Label = "羊了个羊",
			Type = "堆叠消除",
			Desc = "三张同款消除，5 关逐级变难",
			Scene = ScenePaths.SheepGame,
		}, GetNode<Button>("UI/Cards/SheepCard"), CardIcon.Sheep);

		AddGame(new GameEntry
		{
			Label = "扫雷游戏",
			Type = "逻辑 · 排雷",
			Desc = "翻开格子，插旗标出炸弹",
			Scene = ScenePaths.MinesweeperGame,
		}, GetNode<Button>("UI/Cards/MinesweeperCard"), CardIcon.Mine);

		AddGame(new GameEntry
		{
			Label = "蜘蛛纸牌",
			Type = "纸牌 · 接龙",
			Desc = "同花 K 一路排到 A，收满 8 组",
			Scene = ScenePaths.SpiderGame,
		}, GetNode<Button>("UI/Cards/SpiderCard"), CardIcon.Spider);
	}

	private void AddGame(GameEntry e, Button card, CardIcon icon)
	{
		e.Card = card;
		_games.Add(e);

		card.CustomMinimumSize = new Vector2(568, CardH);
		card.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
		card.Text = ""; // 内容全部由子节点排，按钮自己不画文字
		GameArt.StyleButton(card, new Color(1, 1, 1, 0.10f), Colors.White, radius: 28,
			border: new Color(1, 1, 1, 0.30f));
		card.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1, 1, 1, 0.20f), 28, new Color(1, 1, 1, 0.55f)));
		card.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(1, 1, 1, 0.06f), 28, new Color(1, 1, 1, 0.45f)));
		card.AddThemeStyleboxOverride("focus", GameArt.MakeBox(new Color(0, 0, 0, 0f), 28));

		// 卡片的子节点必须 MouseFilter = Ignore，否则它们会把点击吃掉，按钮永远收不到 pressed。
		var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
		row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		row.OffsetLeft = 26;
		row.OffsetRight = -26;
		row.OffsetTop = 16;
		row.OffsetBottom = -16;
		row.AddThemeConstantOverride("separation", 22);
		card.AddChild(row);

		var iconBox = new Control
		{
			CustomMinimumSize = new Vector2(128, 0),
			MouseFilter = MouseFilterEnum.Ignore,
		};
		row.AddChild(iconBox);
		AddIcon(iconBox, icon);

		var col = new VBoxContainer
		{
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		col.AddThemeConstantOverride("separation", 4);
		row.AddChild(col);

		var type = new Label { Text = e.Type, MouseFilter = MouseFilterEnum.Ignore };
		type.AddThemeFontSizeOverride("font_size", 20);
		type.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(type);

		var name = new Label { Text = e.Label, MouseFilter = MouseFilterEnum.Ignore };
		GameArt.OutlineText(name, Colors.White, 42, 6);
		col.AddChild(name);

		var desc = new Label
		{
			Text = e.Desc,
			MouseFilter = MouseFilterEnum.Ignore,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		desc.AddThemeFontSizeOverride("font_size", 20);
		desc.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.72f));
		col.AddChild(desc);
		e.DescLabel = desc;

		var arrow = new Label
		{
			Text = "▶",
			MouseFilter = MouseFilterEnum.Ignore,
			VerticalAlignment = VerticalAlignment.Center,
		};
		arrow.AddThemeFontSizeOverride("font_size", 34);
		arrow.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.55f));
		row.AddChild(arrow);

		card.Pressed += () =>
		{
			PlayPop(card);
			OpenGame(e);
		};
		card.MouseEntered += () => TweenScale(card, 1.035f);
		card.MouseExited += () => TweenScale(card, 1f);
	}

	private void AddIcon(Control box, CardIcon icon)
	{
		// 图标中心：图标盒子是「行高」那么高，行上下各留 16，所以中心在行内 y = (CardH-32)/2
		var at = new Vector2(64, (CardH - 32f) * 0.5f);
		switch (icon)
		{
			case CardIcon.Doll:
				// 有现成素材 → 直接用 Sprite2D（复用游戏里的主角贴纸）
				box.AddChild(new Sprite2D
				{
					Texture = GD.Load<Texture2D>("res://assset/bedtime-and-morning-paper-doll-kit/boy_doll.png"),
					Scale = Vector2.One * 0.15f,
					Position = at,
				});
				break;
			case CardIcon.Ship:
				// 没有素材 → 用 _Draw 现画一架战机（和游戏里是同一套画法）
				box.AddChild(new ShipIcon { Position = at });
				break;
			case CardIcon.Sheep:
				// 卡片变多了，图标也改成「画出来的」：一颗羊头
				box.AddChild(new SheepIcon { Position = at });
				break;
			default:
				// 扫雷：一颗小地雷（和棋盘上是同一个画法）
				box.AddChild(new MineIcon { Position = at });
				break;
			case CardIcon.Spider:
				// 蜘蛛纸牌：两张叠起来的牌 + 一只蜘蛛（八条腿 = 八组 K→A）
				box.AddChild(new SpiderIcon { Position = at });
				break;
		}
	}

	private void BuildHint()
	{
		_hint.Text = "点卡片进入 · 游戏里点「返回」回到这里";
		_hint.HorizontalAlignment = HorizontalAlignment.Center;
		_hint.AddThemeFontSizeOverride("font_size", 22);
		_hint.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.5f));
		_hint.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_hint.OffsetTop = -104;
		_hint.OffsetBottom = -56;
	}

	private void Layout()
	{
		// Control 不会自动填满父节点：尺寸是 0 的话，里面的子控件会全部塌缩成一列看不见的东西
		GetNode<Control>("Stage").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		GetNode<Control>("UI").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		GetNode<Control>("Stage").MouseFilter = MouseFilterEnum.Ignore;
		GetNode<Control>("UI").MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满，并且关掉「最小尺寸 = 纹理尺寸」：
		// TextureRect 的 min size 默认跟着纹理走，而渐变纹理只有 8×256，
		// 不铺满的话背景就只在左上角画一小块，其余全是视口清屏色（0.3 灰）。
		// 贴纸游戏没这个坑，是因为它的背景纹理本来就是 720×1280。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
	}

	// ================= 交互 =================

	private void OpenGame(GameEntry e)
	{
		GD.Print($"[Home] open \"{e.Label}\" -> {e.Scene}");
		var err = GetTree().ChangeSceneToFile(e.Scene);
		if (err != Error.Ok)
			GD.PushError($"[Home] ChangeSceneToFile({e.Scene}) failed: {err}");
	}

	private void PlayPop(Node node)
	{
		if (node is not Control c)
			return;
		c.PivotOffset = c.Size * 0.5f;
		var tw = CreateTween();
		tw.TweenProperty(c, "scale", new Vector2(0.94f, 0.94f), 0.06);
		tw.TweenProperty(c, "scale", Vector2.One, 0.18)
			.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
	}

	private void TweenScale(Control c, float s)
	{
		if (!IsInstanceValid(c) || c.Size.X <= 0f)
			return;
		c.PivotOffset = c.Size * 0.5f;
		var tw = CreateTween();
		tw.TweenProperty(c, "scale", Vector2.One * s, 0.12)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
	}

	/// <summary>入场动画：标题、副标题、两张卡片依次淡入 + 轻微放大。</summary>
	private async Task EnterAnimationAsync()
	{
		_title.Modulate = new Color(1, 1, 1, 0);
		_subtitle.Modulate = new Color(1, 1, 1, 0);
		_hint.Modulate = new Color(1, 1, 1, 0);
		foreach (var g in _games)
			g.Card.Modulate = new Color(1, 1, 1, 0);

		// 等一帧，容器才把卡片排好（这时 Size 才是真实值，PivotOffset 才能算对）
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		var tw = CreateTween();
		tw.SetParallel(true);
		tw.TweenProperty(_title, "modulate:a", 1f, 0.34);
		tw.TweenProperty(_subtitle, "modulate:a", 1f, 0.34).SetDelay(0.08);
		tw.TweenProperty(_hint, "modulate:a", 1f, 0.34).SetDelay(0.34);
		for (int i = 0; i < _games.Count; i++)
		{
			var c = _games[i].Card;
			c.PivotOffset = c.Size * 0.5f;
			c.Scale = Vector2.One * 0.93f;
			float delay = 0.12f + i * 0.10f;
			tw.TweenProperty(c, "modulate:a", 1f, 0.30).SetDelay(delay);
			tw.TweenProperty(c, "scale", Vector2.One, 0.36).SetDelay(delay)
				.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
		}
	}

	// ================= 自测 =================

	private async Task RunSelfTestAsync()
	{
		GD.Print("[SELFTEST] begin (home)");
		await Wait(0.9); // 等入场动画播完，截图才是最终状态

		int fails = 0;

		// ① 卡片全都在，名字对得上
		bool cardsOk = _games.Count == 5 &&
					   _games[0].Label == "贴纸游戏" && _games[1].Label == "雷霆战机" &&
					   _games[2].Label == "羊了个羊" && _games[3].Label == "扫雷游戏" &&
					   _games[4].Label == "蜘蛛纸牌";
		GD.Print($"[SELFTEST] cards: count={_games.Count} [{string.Join(" / ", _games.ConvertAll(g => g.Label))}] -> {cardsOk}");
		if (!cardsOk) fails++;

		// ② 卡片真的被排出了可见尺寸（不是 0×0 的空壳），并且带了内容子节点
		foreach (var g in _games)
		{
			bool ok = g.Card.Size.X > 100f && g.Card.Size.Y > 100f && g.Card.GetChildCount() > 0;
			GD.Print($"[SELFTEST] card \"{g.Label}\" size={g.Card.Size} children={g.Card.GetChildCount()} -> {ok}");
			if (!ok) fails++;
		}

		// ③ 目标场景文件真的存在（防止卡片指向一个删掉/改名的场景）
		foreach (var g in _games)
		{
			bool ok = ResourceLoader.Exists(g.Scene);
			GD.Print($"[SELFTEST] scene exists {g.Scene} -> {ok}");
			if (!ok) fails++;
		}

		// ④ 每个按钮都接上了回调
		foreach (var g in _games)
		{
			bool wired = g.Card.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0;
			GD.Print($"[SELFTEST] \"{g.Label}\" pressed wired: {wired}");
			if (!wired) fails++;
		}

		GD.Print($"[SELFTEST] so far: {(fails == 0 ? "ok" : fails + " problem(s)")}");

		// ②b 卡片的说明文字必须是单行：一旦折行，标题就会一高一低、对不齐。
		//     （卡片高度是固定的，多出来的一行会把上面的内容整体顶上去）。
		foreach (var g in _games)
		{
			int lines = g.DescLabel.GetLineCount();
			bool ok = lines == 1;
			GD.Print($"[SELFTEST] desc single line \"{g.Label}\": {lines} line(s) -> {ok}");
			if (!ok) fails++;
		}

		// ②c 卡片之间不能叠在一起，也不许顶到标题/副标题上（五张卡挤进来之后最容易犯的错）
		for (int i = 0; i < _games.Count; i++)
		{
			var a = _games[i].Card;
			bool topOk = a.GlobalPosition.Y >= _subtitle.GlobalPosition.Y + _subtitle.Size.Y - 1f;
			bool gapOk = i == 0 || a.GlobalPosition.Y >= _games[i - 1].Card.GlobalPosition.Y +
				_games[i - 1].Card.Size.Y - 1f;
			GD.Print($"[SELFTEST] card \"{_games[i].Label}\" y={a.GlobalPosition.Y:0.#} " +
					 $"h={a.Size.Y:0.#} top-clear={topOk} no-overlap={gapOk}");
			if (!topOk || !gapOk) fails++;
		}

		// ⑥ 背景真的铺满了吗（露清屏色就说明背景没覆盖，比如渐变纹理太小导致 TextureRect 只有一小块）
		var shot = GetViewport().GetTexture().GetImage();
		var px0 = new Vector2I((int)(shot.GetWidth() * 0.04f), (int)(shot.GetHeight() * 0.86f));
		var c0 = shot.GetPixel(px0.X, px0.Y);
		bool bgOk = !GameArt.IsClearColor(c0);
		GD.Print($"[SELFTEST] background covers screen: pixel{px0}={c0} -> {bgOk}");
		if (!bgOk) fails++;

		SaveShot("home");

		// ⑤ 触发换场景，校验真的跳过去了。		//    关键：换场景会销毁 Home 自己，所以
		//      a) 校验交给一个挂在树根上、不随场景销毁的见证节点；
		//      b) 这里之后**不能再 await**（await 的续体会跟着 Home 一起死掉）。
		var witness = new SceneWitness(fails);
		GetTree().Root.AddChild(witness);
		_ = witness.VerifyAsync("ThunderGame");
		_games[1].Card.EmitSignal(BaseButton.SignalName.Pressed);
	}

	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Home] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}

	// ================= 内嵌节点 =================
	//
	// 注意：C# 里凡是继承 Godot 节点类型的类（哪怕是嵌套的私有类）都必须写 partial，
	// 否则编译直接报 GD0001（Missing partial modifier ... derives from Godot.GodotObject）。

	/// <summary>背景里慢慢上浮的柔光圆斑（纯装饰）。</summary>
	private sealed partial class DecorBackdrop : Node2D
	{
		private struct Blob
		{
			public Vector2 Pos;
			public float R;
			public Color C;
			public float Speed;
		}

		private Blob[] _blobs = System.Array.Empty<Blob>();

		public override void _Ready()
		{
			// 固定种子：视觉随机但每次运行一样，出问题好复现
			var rng = new System.Random(20260923);
			var palette = new[]
			{
				new Color(0.55f, 0.45f, 1.00f, 0.10f),
				new Color(0.35f, 0.75f, 1.00f, 0.08f),
				new Color(1.00f, 0.60f, 0.85f, 0.07f),
			};
			var list = new List<Blob>();
			for (int i = 0; i < 18; i++)
			{
				list.Add(new Blob
				{
					Pos = new Vector2(rng.Next(0, 720), rng.Next(0, 1280)),
					R = 40f + rng.Next(0, 120),
					C = palette[rng.Next(palette.Length)],
					Speed = 6f + rng.NextSingle() * 12f,
				});
			}
			_blobs = list.ToArray();
		}

		public override void _Process(double delta)
		{
			for (int i = 0; i < _blobs.Length; i++)
			{
				_blobs[i].Pos.Y -= _blobs[i].Speed * (float)delta;
				if (_blobs[i].Pos.Y < -160f)
					_blobs[i].Pos.Y = 1440f;
			}
			QueueRedraw();
		}

		public override void _Draw()
		{
			foreach (var b in _blobs)
				DrawCircle(b.Pos, b.R, b.C);
		}
	}

	/// <summary>卡片上的战机图标（尾焰会晃）。</summary>
	private sealed partial class ShipIcon : Node2D
	{
		private float _t;

		public override void _Process(double delta)
		{
			_t += (float)delta;
			QueueRedraw();
		}

		public override void _Draw()
		{
			GameArt.DrawShip(this, 0.92f, 0.62f + 0.38f * Mathf.Sin(_t * 9f));
		}
	}

	/// <summary>卡片上的羊头图标（一圈羊毛会轻轻呼吸）。</summary>
	private sealed partial class SheepIcon : Node2D
	{
		private float _t;

		public override void _Process(double delta)
		{
			_t += (float)delta;
			QueueRedraw();
		}

		public override void _Draw()
		{
			GameArt.DrawSheepHead(this, 0.95f + 0.05f * Mathf.Sin(_t * 2.4f));
		}
	}

	/// <summary>卡片上的地雷图标（会轻轻呼吸一下，像在晃）。</summary>
	private sealed partial class MineIcon : Node2D
	{
		private float _t;

		public override void _Process(double delta)
		{
			_t += (float)delta;
			QueueRedraw();
		}

		public override void _Draw()
		{
			GameArt.DrawMine(this, 38f * (0.95f + 0.05f * Mathf.Sin(_t * 2.6f)));
		}
	}

	/// <summary>
	/// 卡片上的蜘蛛纸牌图标：两张叠起来的牌 + 正面那张上的一只蜘蛛。
	/// 牌面同样是「画出来」的（花色用 GameArt.DrawSuit，不指望字体里有 ♠）。
	/// </summary>
	private sealed partial class SpiderIcon : Node2D
	{
		private float _t;

		public override void _Process(double delta)
		{
			_t += (float)delta;
			QueueRedraw();
		}

		public override void _Draw()
		{
			// 整个图标轻轻呼吸一下，和旁边几个图标保持一个调子
			DrawSetTransform(Vector2.Zero, 0f, Vector2.One * (0.97f + 0.03f * Mathf.Sin(_t * 2.4f)));

			var back = new Rect2(-32f, -38f, 42f, 74f);
			var front = new Rect2(-10f, -30f, 42f, 74f);

			DrawRect(back, new Color("#2b3f7d"));
			DrawRect(back, new Color("#18254d"), false, 3f);
			DrawRect(front, new Color("#fbfcff"));
			DrawRect(front, new Color("#b9c4dc"), false, 3f);

			// 正面那张：左上角一张黑桃 + 中间一只蜘蛛
			GameArt.DrawSuit(this, 0, new Vector2(front.GetCenter().X, front.Position.Y + 16f), 9f, new Color("#232b3d"));
			GameArt.DrawSpider(this, new Vector2(front.GetCenter().X, front.Position.Y + 47f), 12f,
				new Color("#232b3d"), new Color("#232b3d"));

			DrawSetTransform(Vector2.Zero);
		}
	}

	/// <summary>
	/// 所以把校验放到挂在树根上的这个节点里。
	/// </summary>
	private sealed partial class SceneWitness : Node
	{
		private readonly int _failsBefore;

		public SceneWitness(int failsBefore)
		{
			_failsBefore = failsBefore;
		}

		public async Task VerifyAsync(string expected)
		{
			await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
			string actual = GetTree().CurrentScene?.Name.ToString() ?? "(null)";
			bool ok = actual == expected;
			GD.Print($"[SELFTEST] click card \"雷霆战机\" -> current scene = {actual} (expect {expected}) : {ok}");

			bool passed = ok && _failsBefore == 0;
			GD.Print(passed ? "[SELFTEST] PASSED" : "[SELFTEST] FAILED");

			await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
			GetTree().Quit();
		}
	}
}
