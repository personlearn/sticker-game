#nullable enable
using Godot;
using System;

/// <summary>
/// 一个「玩到一半可以接回来」的场景。<b>只在这一个地方定义</b>，
/// 谁实现它，谁就自动获得「切后台被杀掉也能续上」的能力。
///
/// <para>
/// 为什么需要它：手机上切到微信、或者平板内存吃紧时，Android 会把 app 的进程直接回收。
/// 进程一没，C# 里的字段（穿到第几件衣服、棋盘上还剩几张牌）全跟着消失，
/// 下次点开就是全新的 —— 玩家的感受是「我的进度丢了」。
/// 唯一靠得住的办法是<b>在被打断之前，把进度写进磁盘</b>，下次启动读回来。
/// </para>
///
/// <para>
/// 约定的两条铁律：
/// <list type="number">
/// <item><see cref="Snapshot"/> 只能塞<b>能 JSON 化的东西</b>（int/float/bool/string/数组/字典），
///       不要塞 Node、Texture 这种活对象 —— 它们存不下来。</item>
/// <item><see cref="Restore"/> 只能调各游戏<b>自己的 Apply*/重置入口</b>，
///       不要在这里另写一套「直接摆节点」的代码；否则以后改规则会漏掉这一份。</item>
/// </list>
/// </para>
/// </summary>
public interface IResumable
{
	/// <summary>把当前进度打包成可 JSON 化的字典。必须是纯数据。</summary>
	Godot.Collections.Dictionary Snapshot();

	/// <summary>用快照恢复现场。只会在这个场景 <c>_Ready()</c> 末尾、节点都已就绪时调用。</summary>
	void Restore(Godot.Collections.Dictionary data);
}

/// <summary>
/// 会话快照：<b>唯一</b>负责「切后台 → 落盘 → 冷启动读回 → 回到原场景」的地方。
///
/// <para>
/// 它是 autoload（见 <c>project.godot</c> 的 <c>[autoload]</c>），所以场景怎么换都不会被销毁，
/// 也就不需要每个游戏各写一遍 <c>_Notification</c>。各个场景只要实现 <see cref="IResumable"/>，
/// 就自动被接上。
/// </para>
///
/// <para>
/// 写入时机：<c>NOTIFICATION_APPLICATION_FOCUS_OUT</c> / <c>PAUSED</c> / <c>WM_WINDOW_FOCUS_OUT</c> /
/// <c>WM_CLOSE_REQUEST</c>，外加每 <see cref="AutosaveSeconds"/> 秒的无条件自动存
/// （防「被强行杀掉、连回调都没跑」这种最坏情况）。
/// </para>
///
/// <para>
/// 读回时机：<b>只在冷启动</b>。首页 <c>_Ready()</c> 会问一句「上次是不是玩到一半」，
/// 是就推迟换场景回到那里；停在首页则把档清掉 —— 也就是「在首页 = 没什么要续的」。
/// </para>
/// </summary>
public partial class ResumeState : Node
{
	/// <summary>快照文件。放 user:// 下，卸载 app 才会没。</summary>
	public const string FilePath = "user://resume.json";

	/// <summary>自动存的间隔（秒）。只在「当前场景是 IResumable」时才会真的写。</summary>
	private const double AutosaveSeconds = 12.0;

	/// <summary>启动时读到的「上次那份快照」。只在冷启动那一次有效。</summary>
	private static bool _hasPending;
	private static string _pendingScene = "";
	private static Godot.Collections.Dictionary _pendingState = new();

	private double _acc;
	private CanvasLayer? _layer;
	private Label? _toast;

	public override void _Ready()
	{
		// 切到后台时引擎会暂停整棵树，但我们需要在那之后还能被通知到（以及画个提示），
		// 所以这个节点必须始终在跑。
		ProcessMode = ProcessModeEnum.Always;
		LoadFromDisk();
	}

	public override void _Process(double delta)
	{
		if (_acc < AutosaveSeconds)
		{
			_acc += delta;
			return;
		}
		_acc = 0;
		Commit(silent: true);
	}

	public override void _Notification(int what)
	{
		// 这些常量在 C# 里是 long，所以用 == 比较（int 会被隐式提升），不要写 switch case。
		if (what == NotificationApplicationFocusOut
			|| what == NotificationApplicationPaused
			|| what == NotificationWMWindowFocusOut
			|| what == NotificationWMCloseRequest)
			Commit(silent: false);
	}

	// ===================== 对外 =====================

	/// <summary>「冷启动只认一次」的闸门。见 <see cref="TryPendingScene"/>。</summary>
	private static bool _startupChecked;

	/// <summary>
	/// 上次是不是玩到一半？是就给出那个场景路径（供首页换场景）。
	/// <para>
	/// 三个否决条件，缺一不可地保守：自测期间不恢复（要可复现）、
	/// 场景文件已经不在的不恢复（改过名）、没有快照文件的不恢复。
	/// </para>
	/// <para>
	/// <b>还有第四个、最容易漏的：整个进程只在第一次问的时候算数。</b>
	/// 因为游戏里自动存盘会顺手把「当前场景」记进内存，玩家随后点「返回」回到首页时，
	/// 首页会看到「有个待恢复的档」，于是又把他弹回那个游戏 —— 一次都回不了家。
	/// 所以这个判断必须只对<b>冷启动</b>成立，而不是「只要内存里有档就成立」。
	/// </para>
	/// </summary>
	public static bool TryPendingScene(out string scene)
	{
		scene = _pendingScene;
		if (_startupChecked)
			return false;
		_startupChecked = true;

		if (!_hasPending || _pendingScene.Length == 0)
			return false;
		if (SelftestFlag.Exists())
			return false;
		if (!ResourceLoader.Exists(_pendingScene))
			return false;
		return true;
	}

	/// <summary>
	/// 场景在 <c>_Ready()</c> 末尾问一句「是不是该我恢复」。是则回填数据。
	/// 每个场景只会命中一次（命中后就把待恢复标记清掉），所以不会重复套用。
	/// </summary>
	public static bool TryRestore(Node scene, out Godot.Collections.Dictionary data)
	{
		data = _pendingState;
		if (!_hasPending || _pendingState.Count == 0)
			return false;
		if (scene.SceneFilePath != _pendingScene)
		{
			// 场景对不上：正常情况下只会发生在「玩家自己换了个游戏」的时候。
			// 但如果是「明明该恢复却没恢复」，这里就是唯一能看见原因的地方，所以留一行。
			GD.Print($"[Resume] skip: current={scene.SceneFilePath} pending={_pendingScene}");
			return false;
		}
		_hasPending = false;                 // 消费掉，别让后来的场景又来一次
		ShowRestoredHint(scene);
		return true;
	}

	/// <summary>停在首页 = 没有「玩到一半」的东西，把档清掉。下次启动就是干干净净的首页。</summary>
	public static void Clear()
	{
		_pendingState = new Godot.Collections.Dictionary();
		_pendingScene = "";
		if (_hasPending)
			GD.Print("[Resume] cleared (back at home)");
		_hasPending = false;
		if (!FileAccess.FileExists(FilePath))
			return;
		var dir = DirAccess.Open("user://");
		dir?.Remove("resume.json");
	}

	/// <summary>开新一局 / 结算之后调用：这一局没什么可续的了。</summary>
	public static void Discard()
	{
		Clear();
	}

	// ===================== 读写 =====================

	/// <summary>把<em>当前场景</em>的进度写盘。不是 IResumable 的场景（首页、选人页）直接跳过。</summary>
	private void Commit(bool silent)
	{
		var tree = GetTree();
		Node? scene = tree?.CurrentScene;
		if (scene is not IResumable resumable)
			return;

		Godot.Collections.Dictionary snap;
		try
		{
			snap = resumable.Snapshot();
		}
		catch (Exception e)
		{
			GD.PushWarning($"[Resume] snapshot failed on {scene.Name}: {e.Message}");
			return;
		}

		var root = new Godot.Collections.Dictionary
		{
			{ "scene", scene.SceneFilePath },
			{ "at", Time.GetUnixTimeFromSystem() },
			{ "state", snap },
		};

		using var f = FileAccess.Open(FilePath, FileAccess.ModeFlags.Write);
		if (f == null)
		{
			GD.PushWarning($"[Resume] cannot write {FilePath}: {FileAccess.GetOpenError()}");
			return;
		}
		f.StoreString(Json.Stringify(root));

		// 内存里那份也更新，免得「这次没被杀掉、但存档已经旧了」——
		// 同时保证同一个进程里 Clear() 之后语义一致。
		_pendingScene = scene.SceneFilePath;
		_pendingState = snap;
		_hasPending = true;

		if (!silent)
			GD.Print($"[Resume] saved {scene.Name} ({snap.Count} fields)");
	}

	private void LoadFromDisk()
	{
		if (!FileAccess.FileExists(FilePath))
			return;

		using var f = FileAccess.Open(FilePath, FileAccess.ModeFlags.Read);
		if (f == null)
			return;

		var parsed = Json.ParseString(f.GetAsText());
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			GD.PushWarning("[Resume] resume.json 不是字典，忽略");
			return;
		}

		var root = parsed.AsGodotDictionary();
		if (!root.TryGetValue("scene", out var sceneVar) || !root.TryGetValue("state", out var stateVar))
			return;

		string scene = sceneVar.AsString();
		if (scene.Length == 0 || stateVar.VariantType != Variant.Type.Dictionary)
			return;

		double savedAt = root.TryGetValue("at", out var atVar) ? atVar.AsDouble() : 0;
		int age = savedAt > 0 ? (int)(Time.GetUnixTimeFromSystem() - savedAt) : -1;

		_pendingScene = scene;
		_pendingState = stateVar.AsGodotDictionary();
		_hasPending = true;
		GD.Print($"[Resume] pending: {scene} ({_pendingState.Count} fields, {age}s ago)");
	}

	// ===================== 提示 =====================

	/// <summary>
	/// 「已回到上次玩的地方」。
	/// <para>
	/// 为什么要提示：自动跳回游戏会让玩家以为自己点错了；一句话就把这个疑惑消掉。
	/// 用 CanvasLayer + <c>MouseFilter.Ignore</c>，保证它只是画出来、<b>不吃点击</b>。
	/// </para>
	/// </summary>
	private static void ShowRestoredHint(Node scene)
	{
		if (scene.GetTree()?.Root.GetNodeOrNull<ResumeState>("ResumeState") is not { } self)
			return;
		self.Toast("已回到上次玩的地方");
	}

	private void Toast(string msg)
	{
		if (_toast != null && GodotObject.IsInstanceValid(_toast))
		{
			_toast.QueueFree();
			_toast = null;
		}

		_layer ??= new CanvasLayer { Name = "ResumeToastLayer", Layer = 200 };
		if (_layer.GetParent() == null)
			AddChild(_layer);

		_toast = new Label
		{
			Text = msg,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Modulate = new Color(1, 1, 1, 0),
		};
		_toast.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
		// 顶边钉死、只向两侧长 —— 默认只往右下长，会被裁掉（这个坑踩过好几次了）
		_toast.GrowHorizontal = Control.GrowDirection.Both;
		_toast.GrowVertical = Control.GrowDirection.End;
		_toast.OffsetTop = 96;
		_toast.OffsetBottom = 152;
		_toast.AddThemeFontSizeOverride("font_size", 30);
		_toast.AddThemeColorOverride("font_color", Colors.White);
		_toast.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.82f));
		_toast.AddThemeConstantOverride("outline_size", 12);
		_layer.AddChild(_toast);

		var tw = CreateTween();
		tw.TweenProperty(_toast, "modulate:a", 1f, 0.16);
		tw.TweenInterval(1.5);
		tw.TweenProperty(_toast, "modulate:a", 0f, 0.38);
		tw.TweenCallback(Callable.From(() =>
		{
			if (_toast != null && GodotObject.IsInstanceValid(_toast))
				_toast.QueueFree();
			_toast = null;
		}));
	}

	// ===================== 读数据的小工具 =====================
	// JSON 里所有数字回来都是 double，所以「读个 int」必须显式转，不能直接 AsInt32。

	public static int Int(Godot.Collections.Dictionary d, string key, int def = 0)
		=> d.TryGetValue(key, out var v) ? Int(v, def) : def;

	public static int Int(Variant v, int def = 0)
	{
		return v.VariantType switch
		{
			Variant.Type.Int => v.AsInt32(),
			Variant.Type.Float => Mathf.RoundToInt((float)v.AsDouble()),
			Variant.Type.Bool => v.AsBool() ? 1 : 0,
			_ => def,
		};
	}

	public static float Float(Godot.Collections.Dictionary d, string key, float def = 0f)
		=> d.TryGetValue(key, out var v) ? Float(v, def) : def;

	public static float Float(Variant v, float def = 0f)
	{
		return v.VariantType switch
		{
			Variant.Type.Float => (float)v.AsDouble(),
			Variant.Type.Int => v.AsInt32(),
			_ => def,
		};
	}

	public static bool Bool(Godot.Collections.Dictionary d, string key, bool def = false)
		=> d.TryGetValue(key, out var v) ? v.AsBool() : def;

	public static string Str(Godot.Collections.Dictionary d, string key, string def = "")
		=> d.TryGetValue(key, out var v) ? v.AsString() : def;

	public static Godot.Collections.Array Arr(Godot.Collections.Dictionary d, string key)
		=> d.TryGetValue(key, out var v) && v.VariantType == Variant.Type.Array
			? v.AsGodotArray()
			: new Godot.Collections.Array();

	/// <summary>把 Vector2 拆成 [x, y] 存（JSON 没有 Vector2 这个类型）。</summary>
	public static Godot.Collections.Array Vec(Vector2 v) => new() { v.X, v.Y };

	/// <summary>从 [x, y] 读回 Vector2；格式不对就给 <paramref name="def"/>。</summary>
	public static Vector2 Vec(Variant v, Vector2 def)
	{
		if (v.VariantType != Variant.Type.Array)
			return def;
		var a = v.AsGodotArray();
		if (a.Count < 2)
			return def;
		return new Vector2((float)a[0].AsDouble(), (float)a[1].AsDouble());
	}
}
