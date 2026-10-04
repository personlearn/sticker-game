#nullable enable
using Godot;

/// <summary>
/// 物品栏的「左右箭头翻页器」。
///
/// <para>
/// 换装游戏的物品栏原本是横向 <see cref="ScrollContainer"/>，物品一多就出现一根滚动条。
/// 那根条在手机触屏上又细又滑，孩子基本拖不动。这里把滚动条换成物品栏左右两侧的
/// 两个大号箭头：点一下翻一屏，到底时对应箭头自动变灰。
/// </para>
///
/// <para>
/// 三个细节：
/// <list type="bullet">
/// <item>滚动条只是「不画」（<c>ShowNever</c>），滚动能力还在，大人想直接拖也照样能拖；</item>
/// <item>内容不足一屏时两个箭头直接隐藏（<c>Visible = false</c> 在容器里不占位），
/// 物品栏就能占满整行、多显示一个；</item>
/// <item>箭头靠 <see cref="Range.Changed"/> 信号自己跟着内容刷新，不用各处手动通知。</item>
/// </list>
/// </para>
/// </summary>
public sealed class StripPager
{
	/// <summary>箭头按钮宽度。高度不写死——撑满物品栏那一行，这块区域越大越好按。</summary>
	private const int ArrowWidth = 92;

	/// <summary>点一下翻多远：一屏可视宽度的这个比例。留一点重叠，让人看得出「前面还有东西」。</summary>
	private const float PageRatio = 0.82f;

	private const double AnimTime = 0.24;

	private static readonly Color ArrowInk = new("#3c3c52");
	private static readonly Color ArrowBg = new("#e9ecf9");
	private static readonly Color ArrowBgHover = new("#dce1f5");
	private static readonly Color ArrowBgPressed = new("#cbd4ef");
	private static readonly Color ArrowBgDisabled = new("#f6f7fb");

	private readonly ScrollContainer _scroll;
	private readonly HBoxContainer _row;
	private readonly HBoxContainer _strip;
	private readonly Button _prev;
	private readonly Button _next;
	private readonly System.Action<Button>? _pop;
	private Tween? _tween;

	/// <param name="scroll">场景里的 ItemScroll 节点。构造时会被挪进新的一行里。</param>
	/// <param name="pop">点击时的音效回调，传游戏自己的 PlayPop 即可。</param>
	public StripPager(ScrollContainer scroll, System.Action<Button>? pop = null)
	{
		_scroll = scroll;
		_strip = scroll.GetChild<HBoxContainer>(0);
		_pop = pop;

		// 滚动条藏起来（ShowNever 只是不画，滚动能力仍在，手指照样能拖）
		_scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever;
		_scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;

		// 把原来 VBox 里「物品栏」那一格，换成一整行「箭头 / 物品栏 / 箭头」
		var row = new HBoxContainer { Name = "ItemRow" };
		_row = row;
		row.AddThemeConstantOverride("separation", 10);

		var parent = scroll.GetParent();
		int slot = scroll.GetIndex();
		parent.RemoveChild(scroll);
		parent.AddChild(row);
		parent.MoveChild(row, slot);

		row.AddChild(_prev = MakeArrow(false));
		row.AddChild(scroll);
		row.AddChild(_next = MakeArrow(true));

		// HBox 里必须显式撑开，否则物品栏会缩成内容宽度
		scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

		_prev.Pressed += () => { _pop?.Invoke(_prev); Page(-1); };
		_next.Pressed += () => { _pop?.Invoke(_next); Page(1); };

		// Range.Changed 在 min / max / page 任一变化时都会发，正好同时覆盖
		// 「换了分类、物品数量变了」和「窗口尺寸变了」两种情况，
		// 不用去猜容器什么时候排完版（这里是踩过的坑：CallDeferred 刷新有时赶在排版之前）。
		var bar = _scroll.GetHScrollBar();
		bar.Changed += Refresh;
		bar.ValueChanged += _ => Refresh();

		Callable.From(Refresh).CallDeferred();
	}

	/// <summary>把某个物品滚进可视范围。已可见时不动，所以每次高亮刷新都调也不碍事。</summary>
	public void EnsureVisible(Control item)
	{
		// 这里必须延迟到排版之后再滚，而延迟期间按钮随时可能被 RebuildItemStrip 删掉并释放，
		// 所以执行时要重新确认它还在树里（否则 EnsureControlVisible 会碰已释放对象报错）
		Callable.From(() =>
		{
			if (GodotObject.IsInstanceValid(item) && item.IsInsideTree())
				_scroll.EnsureControlVisible(item);
		}).CallDeferred();
	}

	// ================= 内部 =================

	private void Page(int dir)
	{
		var bar = _scroll.GetHScrollBar();
		float step = Mathf.Max(140f, _scroll.Size.X * PageRatio);
		float target = Mathf.Clamp((float)bar.Value + dir * step, 0f, MaxScroll());

		_tween?.Kill();
		_tween = _scroll.CreateTween();
		_tween.TweenProperty(bar, "value", target, AnimTime)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.Out);
	}

	private float MaxScroll()
	{
		var bar = _scroll.GetHScrollBar();
		return Mathf.Max(0f, (float)(bar.MaxValue - bar.Page));
	}

	/// <summary>内容是不是真的放不下（超过了「不含箭头时」的整行宽度）。</summary>
	private bool NeedsPaging()
	{
		// 判据不能用「箭头占位之后剩下的宽度」：箭头一显示物品栏就变窄，于是永远判为超屏，
		// 箭头再也藏不回去（自锁）。用整行宽度判断，满宽放得下就一点都不占。
		return _scroll.GetHScrollBar().MaxValue > _row.Size.X + 0.5;
	}

	private void Refresh()
	{
		var bar = _scroll.GetHScrollBar();
		float max = MaxScroll();
		bool scrollable = NeedsPaging();

		// 只有变化时才写 Visible：写它会重新排版，排完又回到这里，条件相同就不会再写，避免打转
		if (_prev.Visible != scrollable)
			_prev.Visible = scrollable;
		if (_next.Visible != scrollable)
			_next.Visible = scrollable;

		_prev.Disabled = bar.Value <= 0.5;
		_next.Disabled = bar.Value >= max - 0.5;
	}

	private static Button MakeArrow(bool right)
	{
		var b = new Button
		{
			CustomMinimumSize = new Vector2(ArrowWidth, 0),
			SizeFlagsVertical = Control.SizeFlags.Fill,
			Icon = MakeArrowTexture(right),
			FocusMode = Control.FocusModeEnum.None,   // 触屏用不上键盘焦点，免得留下虚线框
		};
		b.AddThemeStyleboxOverride("normal", GameArt.MakeBox(ArrowBg, 26));
		b.AddThemeStyleboxOverride("hover", GameArt.MakeBox(ArrowBgHover, 26));
		b.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(ArrowBgPressed, 26));
		b.AddThemeStyleboxOverride("disabled", GameArt.MakeBox(ArrowBgDisabled, 26));
		b.AddThemeStyleboxOverride("focus", GameArt.MakeBox(new Color(0, 0, 0, 0), 26));
		// 图标是白色的，靠这组颜色着色；禁用时淡下去
		b.AddThemeColorOverride("icon_normal_color", ArrowInk);
		b.AddThemeColorOverride("icon_hover_color", ArrowInk);
		b.AddThemeColorOverride("icon_pressed_color", new Color("#2f2f45"));
		b.AddThemeColorOverride("icon_disabled_color", new Color(ArrowInk, 0.22f));
		return b;
	}

	/// <summary>画一个实心三角当箭头（不依赖字体里有没有 ◀ ▶ 这些符号）。</summary>
	private static ImageTexture MakeArrowTexture(bool right, int size = 82)
	{
		const int Ss = 4;   // 4 倍超采样再缩回来，边缘才不毛糙
		int n = size * Ss;
		var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
		img.Fill(Colors.Transparent);

		float padY = n * 0.19f;
		float padX = n * 0.27f;
		var a = new Vector2(right ? n - padX : padX, padY);
		var b = new Vector2(right ? n - padX : padX, n - padY);
		var c = new Vector2(right ? padX : n - padX, n * 0.5f);

		for (int y = 0; y < n; y++)
			for (int x = 0; x < n; x++)
				if (InTriangle(new Vector2(x + 0.5f, y + 0.5f), a, b, c))
					img.SetPixel(x, y, Colors.White);

		img.Resize(size, size, Image.Interpolation.Lanczos);
		return ImageTexture.CreateFromImage(img);
	}

	private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
	{
		float d1 = Cross(p - a, b - a);
		float d2 = Cross(p - b, c - b);
		float d3 = Cross(p - c, a - c);
		bool neg = d1 < 0 || d2 < 0 || d3 < 0;
		bool pos = d1 > 0 || d2 > 0 || d3 > 0;
		return !(neg && pos);
	}

	private static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;

	// ================= 自测 =================

	/// <summary>
	/// 自测。分两段：
	/// <list type="number">
	/// <item>看当前真实内容的账面——够一屏就该有箭头、不够就该藏起来；</item>
	/// <item>临时往物品栏里灌 12 个占位块（必然超一屏），把「起点锁左箭头 → 真实点一次右箭头能翻页
	/// → 滚到底锁右箭头」整条链路走一遍，跑完清干净。</item>
	/// </list>
	/// 自己造内容是为了不依赖「此刻正好停在物品最多的那个分类」——
	/// 否则测试强度随游戏数据波动，很容易变成一条永远走不到的死断言。
	/// </summary>
	public async System.Threading.Tasks.Task<bool> SelfTestAsync()
	{
		var tree = _scroll.GetTree();
		var bar = _scroll.GetHScrollBar();

		bool idleOk = NeedsPaging()
			? _prev.Visible && _next.Visible
			: !_prev.Visible && !_next.Visible;

		var probe = new System.Collections.Generic.List<Control>();
		try
		{
			for (int i = 0; i < 12; i++)
			{
				var d = new Control { CustomMinimumSize = new Vector2(150, 100) };
				_strip.AddChild(d);
				probe.Add(d);
			}
			// 两帧：一帧容器排队重排，一帧滚动条跟上
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

			bar.Value = 0;
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			bool atStart = _prev.Visible && _next.Visible && _prev.Disabled && !_next.Disabled;

			// 真实点一下右箭头：鼠标事件 → 引擎的模拟触摸 → 按钮。
			// 这条同时验证了「信号确实连上了」和「按钮真的点得到（没被别的东西盖住）」。
			var vp = _scroll.GetViewport();
			var win = vp.GetFinalTransform() * _next.GetGlobalRect().GetCenter();
			Input.ParseInputEvent(new InputEventMouseMotion { Position = win, GlobalPosition = win });
			Input.ParseInputEvent(new InputEventMouseButton
			{
				Position = win, GlobalPosition = win, ButtonIndex = MouseButton.Left, Pressed = true,
			});
			Input.ParseInputEvent(new InputEventMouseButton
			{
				Position = win, GlobalPosition = win, ButtonIndex = MouseButton.Left, Pressed = false,
			});
			await tree.ToSignal(tree.CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
			bool paged = bar.Value > 4f;

			bar.Value = MaxScroll();
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			bool atEnd = _next.Disabled && !_prev.Disabled;

			GD.Print($"[SELFTEST] strip pager: 账面 {idleOk} / 起点锁左 {atStart} / 点右翻页 {paged} / 到底锁右 {atEnd}");
			return idleOk && atStart && paged && atEnd;
		}
		finally
		{
			foreach (var d in probe)
			{
				_strip.RemoveChild(d);
				d.QueueFree();
			}
		}
	}
}
