#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 贴纸游戏选择页。
///
/// 首页的「贴纸游戏」卡片不再直接进游戏，而是先进这一页——因为现在有几套换装游戏了
/// （<see cref="StickerGame"/> 睡前早晨纸偶 / <see cref="HandDrawnDressUp"/> 手绘小女孩 /
/// <see cref="AnimalAdoptionDressUp"/> 动物领养日纸偶 / <see cref="BakerDressUp"/> 烘焙师纸偶 /
/// <see cref="DoctorDressUp"/> 医生纸偶 / <see cref="StudentDressUp"/> 学生纸偶 /
/// <see cref="WinterDressUp"/> 冬日假期双人纸偶 / <see cref="FamilyDressUp"/> 家庭一家五口纸偶），
/// 得先让玩家挑一个。
/// 这一页只做一件事：几张卡片，点哪张进哪个游戏。
///
/// 各游戏里的「返回」都回到本页（选择页），本页自己的「返回」才回首页。
/// </summary>
public partial class StickerSelect : Control
{
	/// <summary>一张游戏卡片的数据。</summary>
	private sealed class Entry
	{
		public string Label = "";
		public string Type = "";
		public string Desc = "";
		public string Scene = "";
		public string IconTex = "";   // 卡片左侧的素材图（两个游戏都有现成的主角贴纸可用）
		public Button Card = null!;
		public Label DescLabel = null!;
	}

	/// <summary>卡片高度。八张卡要挤进 868 的高度里：8×102 + 7×5 = 851 ≤ 可用 868。</summary>
	private const float CardH = 102f;

	/// <summary>卡片之间的竖直间距。</summary>
	private const float CardGap = 5f;

	/// <summary>卡片左侧图标的状态图区高度（卡片内容区 = CardH - 上下各 10 的外边距）。</summary>
	private const float IconBoxH = CardH - 20f;

	private readonly List<Entry> _entries = new();

	private TextureRect _background = null!;
	private Label _title = null!;
	private Label _subtitle = null!;
	private Label _hint = null!;
	private VBoxContainer _cards = null!;
	private Button _homeButton = null!;

	public override void _Ready()
	{
		_background = GetNode<TextureRect>("Stage/Background");
		_title = GetNode<Label>("UI/Title");
		_subtitle = GetNode<Label>("UI/Subtitle");
		_hint = GetNode<Label>("UI/Hint");
		_cards = GetNode<VBoxContainer>("UI/Cards");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		Theme = GameArt.MakeUiTheme();

		BuildBackground();
		BuildTopBar();
		BuildHeader();
		BuildCards();
		BuildHint();
		Layout();

		GetViewport().SizeChanged += Layout;

		GD.Print($"[StickerSelect] ready. selftest = {SelftestFlag.Describe()}");

		// 自测开关由首页路由过来：只有 selftest.flag 的内容正好是 "select" 时才跑自测。
		if (SelftestFlag.Read() == SelftestFlag.TokenSelect)
			_ = RunSelfTestAsync();
		else
			_ = EnterAnimationAsync();
	}

	// ================= 建界面 =================

	private void BuildBackground()
	{
		// 和首页同一套渐变（竖直渐变用 GradientTexture2D，不必逐像素画 720×1280）
		_background.Texture = GameArt.VerticalGradient(new Color("#123a4d"), new Color("#5a2f63"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;
	}

	private void BuildTopBar()
	{
		var bar = GetNode<HBoxContainer>("UI/TopBar");
		bar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		bar.OffsetLeft = 20;
		bar.OffsetTop = 20;
		bar.OffsetRight = -20;
		bar.OffsetBottom = 112;

		GameArt.StyleButton(_homeButton, new Color("#8f7bff"), Colors.White);
		_homeButton.CustomMinimumSize = new Vector2(150, 92);
		_homeButton.Text = "返回";
		_homeButton.Pressed += OnHomePressed;
	}

	private void BuildHeader()
	{
		_title.Text = "贴纸游戏";
		_title.HorizontalAlignment = HorizontalAlignment.Center;
		GameArt.OutlineText(_title, Colors.White, 68, 8);
		_title.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_title.OffsetTop = 150;
		_title.OffsetBottom = 244;

		_subtitle.Text = "选一个换装游戏开始玩";
		_subtitle.HorizontalAlignment = HorizontalAlignment.Center;
		_subtitle.AddThemeFontSizeOverride("font_size", 28);
		_subtitle.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.66f));
		_subtitle.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_subtitle.OffsetTop = 250;
		_subtitle.OffsetBottom = 296;
	}

	private void BuildCards()
	{
		_cards.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_cards.OffsetTop = 300;
		_cards.OffsetBottom = -112;
		_cards.Alignment = BoxContainer.AlignmentMode.Center;
		_cards.AddThemeConstantOverride("separation", (int)CardGap);
		_cards.MouseFilter = MouseFilterEnum.Ignore;

		// 卡片节点写在 scenes/StickerSelect.tscn 里（顺序就是显示顺序），这里只填内容。
		AddEntry(new Entry
		{
			Label = "睡前早晨换装",
			Type = "纸偶 · 家具贴纸",
			Desc = "换睡衣、摆家具、拍照留念",
			Scene = ScenePaths.StickerGame,
			IconTex = "res://assset/bedtime-and-morning-paper-doll-kit/boy_doll.png",
		}, GetNode<Button>("UI/Cards/SleepCard"));

		AddEntry(new Entry
		{
			Label = "手绘换装",
			Type = "手绘 · 穿裙子",
			Desc = "给手绘小女孩穿裙子、戴发箍",
			Scene = ScenePaths.HandDrawnDressUp,
			IconTex = "res://assset/hand-drawn-dress-up/girl_doll.png",
		}, GetNode<Button>("UI/Cards/HandCard"));

		AddEntry(new Entry
		{
			Label = "领养日换装",
			Type = "纸偶 · 领养宠物",
			Desc = "给小女孩换装、领养小猫小狗",
			Scene = ScenePaths.AnimalAdoptionDressUp,
			IconTex = "res://assset/Animal Adoption Day Paper Doll Playset/girl_doll.png",
		}, GetNode<Button>("UI/Cards/AdoptCard"));

		AddEntry(new Entry
		{
			Label = "烘焙师换装",
			Type = "纸偶 · 烘焙甜点",
			Desc = "系上围裙、烤香喷喷饼干",
			Scene = ScenePaths.BakerDressUp,
			IconTex = "res://assset/Baker Paper Doll/baker_doll.png",
		}, GetNode<Button>("UI/Cards/BakerCard"));

		AddEntry(new Entry
		{
			Label = "医生换装",
			Type = "纸偶 · 打针看病",
			Desc = "穿上白大褂、听诊看病",
			Scene = ScenePaths.DoctorDressUp,
			IconTex = "res://assset/Doctor Paper Doll/doctor_doll.png",
		}, GetNode<Button>("UI/Cards/DoctorCard"));

		AddEntry(new Entry
		{
			Label = "学生换装",
			Type = "纸偶 · 上学去",
			Desc = "穿上校服、背起书包上学",
			Scene = ScenePaths.StudentDressUp,
			IconTex = "res://assset/Student Paper Doll/student_doll.png",
		}, GetNode<Button>("UI/Cards/StudentCard"));

		AddEntry(new Entry
		{
			Label = "冬日假期换装",
			Type = "纸偶 · 双人换装",
			Desc = "给男孩和女孩一起换冬装",
			Scene = ScenePaths.WinterDressUp,
			IconTex = "res://assset/Winter Holiday Paper Doll/cut/boy_doll.png",
		}, GetNode<Button>("UI/Cards/WinterCard"));

		AddEntry(new Entry
		{
			Label = "家庭贴纸换装",
			Type = "纸偶 · 一家五口",
			Desc = "给爸爸、妈妈和孩子一起换装",
			Scene = ScenePaths.FamilyDressUp,
			IconTex = "res://assset/Paper Doll Family/cut/dad_doll.png",
		}, GetNode<Button>("UI/Cards/FamilyCard"));
	}

	private void AddEntry(Entry e, Button card)
	{
		e.Card = card;
		_entries.Add(e);

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
		row.OffsetLeft = 24;
		row.OffsetRight = -24;
		row.OffsetTop = 10;
		row.OffsetBottom = -10;
		row.AddThemeConstantOverride("separation", 18);
		card.AddChild(row);

		var iconBox = new Control
		{
			CustomMinimumSize = new Vector2(118, 0),
			MouseFilter = MouseFilterEnum.Ignore,
		};
		row.AddChild(iconBox);
		AddIcon(iconBox, e.IconTex);

		var col = new VBoxContainer
		{
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Alignment = BoxContainer.AlignmentMode.Center,
		};
		col.AddThemeConstantOverride("separation", 2);
		row.AddChild(col);

		var type = new Label { Text = e.Type, MouseFilter = MouseFilterEnum.Ignore };
		type.AddThemeFontSizeOverride("font_size", 15);
		type.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(type);

		var name = new Label { Text = e.Label, MouseFilter = MouseFilterEnum.Ignore };
		GameArt.OutlineText(name, Colors.White, 24, 5);
		col.AddChild(name);

		var desc = new Label
		{
			Text = e.Desc,
			MouseFilter = MouseFilterEnum.Ignore,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		desc.AddThemeFontSizeOverride("font_size", 15);
		desc.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.72f));
		col.AddChild(desc);
		e.DescLabel = desc;

		var arrow = new Label
		{
			Text = "▶",
			MouseFilter = MouseFilterEnum.Ignore,
			VerticalAlignment = VerticalAlignment.Center,
		};
		arrow.AddThemeFontSizeOverride("font_size", 26);
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

	/// <summary>卡片左侧的主角贴纸：按高度收进图标盒子，居中摆放。</summary>
	private void AddIcon(Control box, string texPath)
	{
		var tex = GD.Load<Texture2D>(texPath);
		if (tex == null)
		{
			GD.PushError($"[StickerSelect] icon texture missing: {texPath}");
			return;
		}
		// 图标盒子的高度由行高决定（CardH - 上下 16），取 0.92 倍留一点边
		float target = IconBoxH * 0.92f;
		float s = target / Mathf.Max(tex.GetHeight(), 1);
		box.AddChild(new Sprite2D
		{
			Texture = tex,
			Scale = Vector2.One * s,
			Position = new Vector2(59f, IconBoxH * 0.5f),
		});
	}

	private void BuildHint()
	{
		_hint.Text = "点卡片进入 · 游戏里点「返回」回到贴纸游戏列表";
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

		// 背景必须显式铺满，并关掉「最小尺寸 = 纹理尺寸」（渐变纹理只有 8×256，
		// 不然背景只在左上角画一小块，其余露出清屏色）。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
	}

	// ================= 交互 =================

	/// <summary>回首页。</summary>
	private void OnHomePressed()
	{
		PlayPop(_homeButton);
		var err = GetTree().ChangeSceneToFile(ScenePaths.Home);
		if (err != Error.Ok)
			GD.PushError($"[StickerSelect] ChangeSceneToFile(Home) failed: {err}");
	}

	private void OpenGame(Entry e)
	{
		GD.Print($"[StickerSelect] open \"{e.Label}\" -> {e.Scene}");
		var err = GetTree().ChangeSceneToFile(e.Scene);
		if (err != Error.Ok)
			GD.PushError($"[StickerSelect] ChangeSceneToFile({e.Scene}) failed: {err}");
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

	/// <summary>入场动画：标题、副标题、几张卡片依次淡入 + 轻微放大。</summary>
	private async Task EnterAnimationAsync()
	{
		_title.Modulate = new Color(1, 1, 1, 0);
		_subtitle.Modulate = new Color(1, 1, 1, 0);
		_hint.Modulate = new Color(1, 1, 1, 0);
		_homeButton.Modulate = new Color(1, 1, 1, 0);
		foreach (var e in _entries)
			e.Card.Modulate = new Color(1, 1, 1, 0);

		// 等一帧，容器才把卡片排好（这时 Size 才是真实值，PivotOffset 才能算对）
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		var tw = CreateTween();
		tw.SetParallel(true);
		tw.TweenProperty(_title, "modulate:a", 1f, 0.34);
		tw.TweenProperty(_subtitle, "modulate:a", 1f, 0.34).SetDelay(0.08);
		tw.TweenProperty(_homeButton, "modulate:a", 1f, 0.34).SetDelay(0.08);
		tw.TweenProperty(_hint, "modulate:a", 1f, 0.34).SetDelay(0.34);
		for (int i = 0; i < _entries.Count; i++)
		{
			var c = _entries[i].Card;
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
		GD.Print("[SELFTEST] begin (select)");
		await Wait(0.5);

		int fails = 0;

		// ① 八张卡片都在、名字对得上
		bool cardsOk = _entries.Count == 8 &&
					   _entries[0].Label == "睡前早晨换装" && _entries[1].Label == "手绘换装" &&
					   _entries[2].Label == "领养日换装" && _entries[3].Label == "烘焙师换装" &&
					   _entries[4].Label == "医生换装" && _entries[5].Label == "学生换装" &&
					   _entries[6].Label == "冬日假期换装" && _entries[7].Label == "家庭贴纸换装";
		GD.Print($"[SELFTEST] cards: count={_entries.Count} [{string.Join(" / ", _entries.ConvertAll(e => e.Label))}] -> {cardsOk}");
		if (!cardsOk) fails++;

		// ② 卡片真的被排出了可见尺寸、带了内容子节点，而且说明文字是单行（折行会把卡片顶乱）
		foreach (var e in _entries)
		{
			bool ok = e.Card.Size.X > 100f && e.Card.Size.Y > 100f && e.Card.GetChildCount() > 0;
			GD.Print($"[SELFTEST] card \"{e.Label}\" size={e.Card.Size} children={e.Card.GetChildCount()} -> {ok}");
			if (!ok) fails++;

			int lines = e.DescLabel.GetLineCount();
			GD.Print($"[SELFTEST] desc single line \"{e.Label}\": {lines} line(s) -> {lines == 1}");
			if (lines != 1) fails++;
		}

		// ③ 目标场景文件真的存在（防止卡片指向一个删掉/改名的场景）
		foreach (var e in _entries)
		{
			bool ok = ResourceLoader.Exists(e.Scene);
			GD.Print($"[SELFTEST] scene exists {e.Scene} -> {ok}");
			if (!ok) fails++;
		}

		// ④ 每个按钮都接上了回调
		foreach (var e in _entries)
		{
			bool wired = e.Card.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0;
			GD.Print($"[SELFTEST] \"{e.Label}\" pressed wired: {wired}");
			if (!wired) fails++;
		}

		// ⑤ 几张卡片不能叠在一起，也不许顶到副标题上
		for (int i = 0; i < _entries.Count; i++)
		{
			var a = _entries[i].Card;
			bool topOk = a.GlobalPosition.Y >= _subtitle.GlobalPosition.Y + _subtitle.Size.Y - 1f;
			bool gapOk = i == 0 || a.GlobalPosition.Y >= _entries[i - 1].Card.GlobalPosition.Y +
				_entries[i - 1].Card.Size.Y - 1f;
			GD.Print($"[SELFTEST] card \"{_entries[i].Label}\" y={a.GlobalPosition.Y:0.#} " +
					 $"h={a.Size.Y:0.#} top-clear={topOk} no-overlap={gapOk}");
			if (!topOk || !gapOk) fails++;
		}

		// ⑥ 「返回」按钮接上了回调
		bool homeWired = _homeButton.Text == "返回" &&
						 _homeButton.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0;
		GD.Print($"[SELFTEST] top bar: \"{_homeButton.Text}\" wired={homeWired}");
		if (!homeWired) fails++;

		// ⑦ 背景真的铺满了吗（露清屏色就说明背景没覆盖）
		var shot = GetViewport().GetTexture().GetImage();
		var px0 = new Vector2I((int)(shot.GetWidth() * 0.04f), (int)(shot.GetHeight() * 0.86f));
		var c0 = shot.GetPixel(px0.X, px0.Y);
		bool bgOk = !GameArt.IsClearColor(c0);
		GD.Print($"[SELFTEST] background covers screen: pixel{px0}={c0} -> {bgOk}");
		if (!bgOk) fails++;

		SaveShot("select");

		// ⑧ 真实点击第二张卡（手绘换装），校验真的换过去了。
		//    换场景会销毁本场景，所以：
		//      a) 校验交给挂在树根上、不随场景销毁的见证节点；
		//      b) 这行之后**不能再 await**（await 的续体会跟着本场景一起死掉）。
		var witness = new SceneWitness(fails);
		GetTree().Root.AddChild(witness);
		_ = witness.VerifyAsync("HandDrawnDressUp");
		_entries[1].Card.EmitSignal(BaseButton.SignalName.Pressed);
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
		GD.Print($"[StickerSelect] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}

	/// <summary>
	/// 「换场景之后」的见证者。本场景一换就被销毁，它自己的协程会跟着死，
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
			GD.Print($"[SELFTEST] click card \"手绘换装\" -> current scene = {actual} (expect {expected}) : {ok}");

			bool passed = ok && _failsBefore == 0;
			GD.Print(passed ? "[SELFTEST] PASSED" : "[SELFTEST] FAILED");

			await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
			GetTree().Quit();
		}
	}
}