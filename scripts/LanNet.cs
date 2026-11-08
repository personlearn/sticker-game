#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// 局域网两人对战的通信层。两个手机在同一个 Wi-Fi 下，一个建房、一个加入，
/// 全程只走本地网络，不经过任何服务器。
///
/// <para>
/// 为什么是 TCP 而不是 ENet：围棋是回合制，一手棋要的是「一定送到、一定按顺序」，
/// 丢一帧重发反而更麻烦。TCP 天然有序可靠，而且可以用 <see cref="TcpServer"/> +
/// <see cref="StreamPeerTcp"/> 在同一个进程里开一对，端到端测起来很省事。
/// </para>
///
/// <para>
/// 消息是「一行一条 JSON」（<c>\n</c> 分隔）：JSON 序列化时会把换行转义掉，
/// 所以按行切一定安全，不用自己算长度前缀。
/// </para>
///
/// <para>
/// 发现房间用 UDP 广播（<see cref="DiscoverPort"/>）：房主每 0.8 秒喊一声
/// 「我叫什么、几路」，客方只管听。广播收不到也不要紧，界面里还有手输 IP 的口子。
/// </para>
/// </summary>
public sealed class LanNet : IDisposable
{
	/// <summary>对战用的 TCP 端口（房主监听）。</summary>
	public const int GamePort = 47800;

	/// <summary>房间发现的 UDP 广播端口（只有客方监听，房主只发不听）。</summary>
	public const int DiscoverPort = 47801;

	/// <summary>广播包前缀，用来筛掉同一端口上别人的包。</summary>
	private const string Magic = "STICKERGO/1";

	/// <summary>多久喊一次「我在这儿」。太密没必要，太慢加入时要干等。</summary>
	private const float BroadcastEvery = 0.8f;

	/// <summary>广播列表里一条记录多久算过期。房主拔网线时会停喊，靠这个自然消失。</summary>
	private const ulong RoomTtlMs = 3200;

	// ===================== 事件 =====================

	public enum EvKind
	{
		/// <summary>收到一条消息，<see cref="Ev.Type"/> 是消息名，<see cref="Ev.A"/> 是数字参数。</summary>
		Msg,
		/// <summary>连接建立（房主：有人进来；客方：连上了）。</summary>
		Joined,
		/// <summary>对端断开。</summary>
		Left,
		/// <summary>出错（连不上 / 端口被占）。</summary>
		Failed,
	}

	/// <summary>一次「轮到游戏层处理」的事情。用队列而不是回调：网络收包在 Update 里，
	/// 但改棋盘必须在游戏自己的节奏上，堆成队列一次性取走最省心。</summary>
	public sealed class Ev
	{
		public EvKind Kind;
		public string Type = "";
		public string Text = "";
		public int A;
	}

	/// <summary>广播里收到的一个房间。</summary>
	public sealed class Room
	{
		public string Name = "";
		public string Ip = "";
		public int Level;
		public ulong Seen;
	}

	// ===================== 状态 =====================

	public string MyName = "";
	public string PeerName = "";
	public bool IsHost { get; private set; }
	public bool Connected { get; private set; }

	/// <summary>既没建房、也没在连，只是开着广播监听 —— 面板据此显示「创建 / 加入」。
	/// 光看 <see cref="Connected"/> 分不出来：等着的房主和正在连的客方都是 false。</summary>
	public bool Idle => _server == null && _peer == null && !Connected;
	public bool IsScanning => _listener != null;
	public string LastError = "";

	private readonly int _gamePort;
	private readonly List<Room> _rooms = new();
	private readonly List<Ev> _events = new();
	private readonly List<byte> _rx = new();

	private TcpServer? _server;
	private StreamPeerTcp? _peer;
	private PacketPeerUdp? _sender;
	private PacketPeerUdp? _listener;
	private float _broadcastCd;
	private int _level;
	private bool _connecting;

	public LanNet(int gamePort = GamePort)
	{
		_gamePort = gamePort;
	}

	public IReadOnlyList<Room> Rooms => _rooms;

	/// <summary>
	/// 房主界面上那句「让对方输这个地址」。
	/// 一台机器往往有好几个 IPv4（WSL、Hyper-V、VirtualBox 都各占一个 172.x），
	/// 挑错了对方就永远连不上，所以按「家宽常见程度」排个序：192.168 最像真 Wi-Fi。
	/// </summary>
	public static string FirstLanIp()
	{
		string best = "";
		int bestRank = int.MaxValue;
		foreach (string ip in IP.GetLocalAddresses())
		{
			if (ip.Contains(':'))
				continue; // IPv6 手输太长，先不管
			int rank = ip switch
			{
				_ when ip.StartsWith("192.168.") => 0,
				_ when ip.StartsWith("10.") => 1,
				_ when IsPrivate172(ip) => 2,
				_ when ip.StartsWith("169.254.") => 9, // 没要上 DHCP 的自留地址，基本没用
				_ => 5,
			};
			if (rank < bestRank)
			{
				bestRank = rank;
				best = ip;
			}
		}
		return best.Length > 0 ? best : "127.0.0.1";
	}

	/// <summary>172.16.0.0 ~ 172.31.255.255 才是私网段（172.20.x 之类是公网）。</summary>
	private static bool IsPrivate172(string ip)
	{
		if (!ip.StartsWith("172."))
			return false;
		int dot = ip.IndexOf('.', 4);
		if (dot < 0)
			return false;
		return int.TryParse(ip[4..dot], out int second) && second is >= 16 and <= 31;
	}

	/// <summary>随机昵称。两台设备各起一个，握个手交换一下就能在 HUD 上互称。</summary>
	public static string RandomName()
	{
		string[] adj = { "安静的", "打瞌睡的", "慢半拍的", "亮晶晶的", "圆滚滚的", "笑眯眯的", "会下棋的", "好奇的" };
		string[] noun = { "棋子", "熊猫", "灯笼", "陶罐", "小船", "毛线团", "小蘑菇", "纸飞机" };
		var rng = new Random((int)(Time.GetTicksMsec() & 0x7FFFFFFF) ^ (int)GD.Randi());
		return $"{adj[rng.Next(adj.Length)]}{noun[rng.Next(noun.Length)]} {rng.Next(100, 999)}";
	}

	// ===================== 房主 =====================

	public bool StartHost(string name, int level)
	{
		MyName = name;
		_level = level;
		IsHost = true;

		_server = new TcpServer();
		Error err = _server.Listen((ushort)_gamePort, "*");
		if (err != Error.Ok)
		{
			LastError = $"端口 {_gamePort} 被占用（{err}），可能开着一个没关掉的房间";
			_server = null;
			IsHost = false;
			Push(EvKind.Failed, "", LastError);
			return false;
		}

		// 广播发射器：绑 0 号端口拿一个临时口，和客方监听的 47801 不冲突
		// （同一台机器上开两个实例自测时，这一点是必须的）。
		_sender = new PacketPeerUdp();
		if (_sender.Bind(0, "*") == Error.Ok)
		{
			_sender.SetBroadcastEnabled(true);
			_sender.SetDestAddress("255.255.255.255", DiscoverPort);
		}
		else
		{
			_sender = null; // 广播发不出去不影响手输 IP 加入
		}

		_broadcastCd = 0f;
		LastError = "";
		return true;
	}

	// ===================== 客方 =====================

	/// <summary>开始听广播。收不到也不要紧，界面里还能手输 IP。</summary>
	public void StartScan()
	{
		if (_listener != null)
			return;
		var udp = new PacketPeerUdp();
		if (udp.Bind(DiscoverPort, "*") != Error.Ok)
		{
			// 多半是同一个端口被别的实例占着（或上一次没关干净）
			LastError = $"听不到广播（UDP {DiscoverPort} 被占用），可以手输对方 IP";
			return;
		}
		udp.SetBroadcastEnabled(true);
		_listener = udp;
	}

	public bool Connect(string ip)
	{
		ip = ip.Trim();
		if (ip.Length == 0)
			return false;

		ClosePeer();
		IsHost = false;
		_peer = new StreamPeerTcp();
		Error err = _peer.ConnectToHost(ip, _gamePort);
		if (err != Error.Ok)
		{
			LastError = $"连不上 {ip}（{err}）";
			_peer = null;
			Push(EvKind.Failed, "", LastError);
			return false;
		}
		_connecting = true;
		LastError = "";
		return true;
	}

	// ===================== 每帧 =====================

	public void Update(float delta)
	{
		PollServer();
		PollPeer();
		PollListener();
		PollBroadcast(delta);
	}

	private void PollServer()
	{
		if (_server == null || !_server.IsConnectionAvailable())
			return;

		StreamPeerTcp incoming = _server.TakeConnection();
		if (Connected)
		{
			// 已经有人在下了，第三个人连进来直接挂断
			incoming.DisconnectFromHost();
			return;
		}

		_peer = incoming;
		Connected = true;
		_rx.Clear();
		Push(EvKind.Joined);
		SendHi(); // 先自报家门，对方拿到昵称才知道 HUD 上写谁
	}

	private void PollPeer()
	{
		if (_peer == null)
			return;

		_peer.Poll();
		StreamPeerTcp.Status st = _peer.GetStatus();

		if (st == StreamPeerTcp.Status.Connected)
		{
			if (!Connected)
			{
				Connected = true;
				_connecting = false;
				_rx.Clear();
				Push(EvKind.Joined);
				SendHi();
			}
			ReadAvailable();
		}
		else if (st == StreamPeerTcp.Status.Error || st == StreamPeerTcp.Status.None)
		{
			if (Connected)
			{
				Connected = false;
				Push(EvKind.Left);
			}
			else if (_connecting)
			{
				// 还没连上就 Error：对方没开房间 / 地址不对 / 防火墙挡了。
				// 这条一定要报出来，否则界面会永远停在「正在连接…」。
				Push(EvKind.Failed, "", "连不上对方 · 检查地址、是否同一个 Wi-Fi、对方房间还开着吗");
			}
			_connecting = false;
			_peer = null;
			_rx.Clear();
		}
	}

	private void ReadAvailable()
	{
		if (_peer == null)
			return;

		int avail = _peer.GetAvailableBytes();
		if (avail <= 0)
			return;

		// Godot 4.7 的 GetData 返回的是「两个值」：一个 Error + 一个 PackedByteArray，
		// 在 C# 里就是 Array[0] = Error、Array[1] = byte[]
		Godot.Collections.Array chunk = _peer.GetData(avail);
		if (chunk.Count < 2 || chunk[0].AsInt32() != (int)Error.Ok)
			return;
		byte[] data = chunk[1].AsByteArray();
		if (data.Length == 0)
			return;

		foreach (byte b in data)
		{
			if (b == (byte)'\n')
			{
				string line = Encoding.UTF8.GetString(_rx.ToArray()).Trim();
				_rx.Clear();
				if (line.Length > 0)
					HandleLine(line);
			}
			else
			{
				_rx.Add(b);
				// 防御性上限：对面发疯也不会把内存吃光
				if (_rx.Count > 8192)
					_rx.Clear();
			}
		}
	}

	private void HandleLine(string line)
	{
		Variant v = Json.ParseString(line);
		if (v.VariantType != Variant.Type.Dictionary)
			return;

		Godot.Collections.Dictionary d = v.AsGodotDictionary();
		string type = d.TryGetValue("t", out Variant tv) ? tv.AsString() : "";
		if (type.Length == 0)
			return;

		var ev = new Ev { Kind = EvKind.Msg, Type = type };
		if (d.TryGetValue("p", out Variant pv))
			ev.A = pv.AsInt32();
		if (d.TryGetValue("lv", out Variant lv))
			ev.A = lv.AsInt32();
		if (d.TryGetValue("name", out Variant nv))
			ev.Text = nv.AsString();

		if (type == "hi" && ev.Text.Length > 0)
			PeerName = ev.Text;

		_events.Add(ev);
	}

	/// <summary>收广播（只有客方会绑端口）。</summary>
	private void PollListener()
	{
		if (_listener == null)
			return;

		while (_listener.GetAvailablePacketCount() > 0)
		{
			byte[] packet = _listener.GetPacket();
			string ip = _listener.GetPacketIP();
			string txt = Encoding.UTF8.GetString(packet);
			if (!TryParseRoom(txt, out string name, out int level))
				continue;
			UpsertRoom(name, level, ip);
		}

		ulong now = Time.GetTicksMsec();
		for (int i = _rooms.Count - 1; i >= 0; i--)
			if (now - _rooms[i].Seen > RoomTtlMs)
				_rooms.RemoveAt(i);
	}

	/// <summary>
	/// 房主隔一会儿喊一声「我在这儿」。
	/// 注意这段**不能**放在 PollListener 里：房主根本不绑监听端口（绑定会和自己冲突），
	/// 一旦写在「有监听才发」的分支里，房主就永远不广播，客方永远搜不到房间。
	/// </summary>
	private void PollBroadcast(float delta)
	{
		if (_sender == null)
			return;

		_broadcastCd -= delta;
		if (_broadcastCd > 0f)
			return;

		_broadcastCd = BroadcastEvery;
		byte[] payload = Encoding.UTF8.GetBytes(PackRoom(MyName, _level));
		if (_sender.PutPacket(payload) != Error.Ok)
			LastError = "广播发不出去（不影响手输 IP 加入）";
	}

	private void UpsertRoom(string name, int level, string ip)
	{
		foreach (Room r in _rooms)
		{
			if (r.Ip != ip)
				continue;
			r.Name = name;
			r.Level = level;
			r.Seen = Time.GetTicksMsec();
			return;
		}
		_rooms.Add(new Room { Name = name, Level = level, Ip = ip, Seen = Time.GetTicksMsec() });
	}

	// ===================== 广播包格式 =====================

	/// <summary>「STICKERGO/1|路数|昵称」。昵称放最后，里面有 | 也不影响解析。</summary>
	public static string PackRoom(string name, int level) => $"{Magic}|{level}|{name}";

	public static bool TryParseRoom(string packet, out string name, out int level)
	{
		name = "";
		level = 0;
		if (!packet.StartsWith(Magic + "|"))
			return false;
		string rest = packet[(Magic.Length + 1)..];
		int bar = rest.IndexOf('|');
		if (bar <= 0)
			return false;
		if (!int.TryParse(rest[..bar], out level))
			return false;
		if (level < 0 || level > 2)
			return false;
		name = rest[(bar + 1)..];
		return name.Length > 0;
	}

	// ===================== 发消息 =====================

	private void SendRaw(string json)
	{
		if (_peer == null || !Connected)
			return;
		byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
		_peer.PutData(bytes);
	}

	public void SendHi()
		=> SendRaw($"{{\"t\":\"hi\",\"name\":\"{Escape(MyName)}\"}}");

	public void SendStart(int level)
		=> SendRaw($"{{\"t\":\"start\",\"lv\":{level}}}");

	public void SendMove(int p)
		=> SendRaw($"{{\"t\":\"move\",\"p\":{p}}}");

	public void SendPass() => SendRaw("{\"t\":\"pass\"}");
	public void SendUndo() => SendRaw("{\"t\":\"undo\"}");
	public void SendResume() => SendRaw("{\"t\":\"resume\"}");
	public void SendRestart() => SendRaw("{\"t\":\"restart\"}");
	public void SendBad() => SendRaw("{\"t\":\"bad\"}");
	public void SendBye() => SendRaw("{\"t\":\"bye\"}");

	private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

	// ===================== 收尾 =====================

	public List<Ev> Drain()
	{
		if (_events.Count == 0)
			return _empty;
		var copy = new List<Ev>(_events);
		_events.Clear();
		return copy;
	}

	private readonly List<Ev> _empty = new();

	private void Push(EvKind kind, string type = "", string text = "")
		=> _events.Add(new Ev { Kind = kind, Type = type, Text = text });

	private void ClosePeer()
	{
		if (_peer != null)
		{
			if (Connected)
				_peer.DisconnectFromHost();
			_peer = null;
		}
		Connected = false;
		_rx.Clear();
	}

	/// <summary>房主停下来：关监听、关广播、断掉那个人。客方调用就是「退出房间」。</summary>
	public void Close()
	{
		if (Connected)
			SendBye();

		_sender?.Close();
		_sender = null;

		_server?.Stop();
		_server = null;

		ClosePeer();

		_listener?.Close();
		_listener = null;

		_rooms.Clear();
		_events.Clear();
		IsHost = false;
	}

	public void Dispose() => Close();
}
