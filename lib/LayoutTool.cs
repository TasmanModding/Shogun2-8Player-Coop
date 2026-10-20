// Reader/writer for Shogun 2 UI layout files (Version054 only), written from re/LAYOUT_FORMAT.md.
// Every field is kept, in file order, so Write(Read(file)) must reproduce the file byte-for-byte.
// Load from PowerShell with: Add-Type -Path "re\scripts\LayoutTool.cs"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// T: 'b' u8, 'u' u32, 'i' i32, 's' str (raw bytes), 'w' wstr (raw UTF-16LE bytes),
//    'L' u32-counted list of nodes, 'E' event list (ends with "events_end"), 'B' nested block node
public class LField { public string Name; public char T; public object V; }

public class LNode {
	public string Kind; public int Offset; public int End;
	public List<LField> F = new List<LField>();

	public LField Field(string name) {
		foreach (var f in F) if (f.Name == name) return f;
		return null;
	}
	public string Str(string name) { var f = Field(name); return f == null ? null : Layout.L1.GetString((byte[])f.V); }
	public void SetStr(string name, string value) { Field(name).V = Layout.L1.GetBytes(value); }
	public uint U(string name) { return (uint)Field(name).V; }
	public int I(string name) { return (int)Field(name).V; }
	public List<LNode> List(string name) { var f = Field(name); return f == null ? new List<LNode>() : (List<LNode>)f.V; }
	public LNode Block() { var f = Field("block"); return f == null ? null : (LNode)f.V; }
}

public class Layout {
	public static readonly Encoding L1 = Encoding.GetEncoding(28591);  // 1:1 byte <-> char
	public const string Header = "Version054";

	byte[] b; int p; readonly Stack<string> path = new Stack<string>();

	// ---------- reading ----------
	public static LNode Read(byte[] data) {
		if (data.Length < 10 || L1.GetString(data, 0, 10) != Header) throw new InvalidDataException("not a " + Header + " layout");
		var r = new Layout { b = data, p = 10 };
		LNode root;
		try { root = r.Component(); }
		catch (Exception e) { throw new InvalidDataException(string.Format("{0} at 0x{1:X} in {2}", e.Message, r.p, string.Join(" > ", r.path.Reverse())), e); }
		if (r.p != data.Length) throw new InvalidDataException(string.Format("parsed to 0x{0:X} but file is 0x{1:X} bytes", r.p, data.Length));
		return root;
	}

	void Need(int n) { if (n < 0 || p + n > b.Length) throw new EndOfStreamException("unexpected end of data (need " + n + ")"); }
	byte RU8() { Need(1); return b[p++]; }
	ushort RU16() { Need(2); var v = BitConverter.ToUInt16(b, p); p += 2; return v; }
	uint RU32() { Need(4); var v = BitConverter.ToUInt32(b, p); p += 4; return v; }
	byte[] RBytes(int n) { Need(n); var v = new byte[n]; Buffer.BlockCopy(b, p, v, 0, n); p += n; return v; }

	void B8(LNode n, string name) { n.F.Add(new LField { Name = name, T = 'b', V = RU8() }); }
	void U32(LNode n, string name) { n.F.Add(new LField { Name = name, T = 'u', V = RU32() }); }
	void I32(LNode n, string name) { Need(4); n.F.Add(new LField { Name = name, T = 'i', V = BitConverter.ToInt32(b, p) }); p += 4; }
	void S(LNode n, string name) { int len = RU16(); n.F.Add(new LField { Name = name, T = 's', V = RBytes(len) }); }
	void W(LNode n, string name) { int len = RU16(); n.F.Add(new LField { Name = name, T = 'w', V = RBytes(len * 2) }); }
	void L(LNode n, string name, Func<LNode> item) {
		uint count = RU32();
		if (count > 100000) throw new InvalidDataException("implausible count " + count + " for " + name);
		var list = new List<LNode>();
		for (uint i = 0; i < count; i++) list.Add(item());
		n.F.Add(new LField { Name = name, T = 'L', V = list });
	}
	LNode Begin(string kind) { return new LNode { Kind = kind, Offset = p }; }
	LNode Done(LNode n) { n.End = p; return n; }

	LNode Component() {
		var n = Begin("component");
		U32(n, "uid"); S(n, "name"); path.Push(n.Str("name"));
		S(n, "class"); I32(n, "x"); I32(n, "y");
		B8(n, "f1"); B8(n, "f2"); B8(n, "f3"); U32(n, "f4"); B8(n, "f5"); B8(n, "f6"); B8(n, "f7"); B8(n, "f8"); B8(n, "f9");
		S(n, "s1"); U32(n, "u1");
		W(n, "w1"); W(n, "w2");
		U32(n, "u2"); B8(n, "b2");
		U32(n, "default_state");
		S(n, "s2");
		L(n, "images", Image);
		U32(n, "image_ref");
		U32(n, "u3");
		L(n, "states", State);
		L(n, "properties", Property);
		Events(n);
		U32(n, "u4");
		L(n, "anims", Anim);
		L(n, "children", Component);
		S(n, "template");
		byte hasBlock = RU8();
		n.F.Add(new LField { Name = "has_block", T = 'b', V = hasBlock });
		if (hasBlock != 0) n.F.Add(new LField { Name = "block", T = 'B', V = BlockNode() });
		S(n, "s_last");
		path.Pop();
		return Done(n);
	}

	LNode Image() {
		var n = Begin("image");
		U32(n, "uid"); S(n, "path"); U32(n, "w"); U32(n, "h"); U32(n, "extra");
		return Done(n);
	}

	LNode State() {
		var n = Begin("state");
		U32(n, "uid"); S(n, "name"); path.Push("state:" + n.Str("name"));
		I32(n, "w"); I32(n, "h"); W(n, "text"); W(n, "tooltip");
		I32(n, "a1"); I32(n, "a2"); I32(n, "a3"); I32(n, "a4"); I32(n, "a5");
		B8(n, "b1"); W(n, "w3"); W(n, "w4");
		S(n, "font"); I32(n, "c1"); I32(n, "c2"); U32(n, "colour"); S(n, "font_category");
		I32(n, "d1"); I32(n, "d2"); I32(n, "d3");
		B8(n, "e1"); B8(n, "e2"); B8(n, "e3");
		S(n, "shader"); I32(n, "sh1"); I32(n, "sh2"); I32(n, "sh3"); I32(n, "sh4");
		S(n, "s1"); S(n, "s2");
		L(n, "state_images", StateImage);
		I32(n, "ox"); I32(n, "oy");
		L(n, "transitions", Transition);
		path.Pop();
		return Done(n);
	}

	LNode StateImage() {
		var n = Begin("state_image");
		U32(n, "image"); I32(n, "x"); I32(n, "y"); I32(n, "w"); I32(n, "h"); U32(n, "colour");
		B8(n, "t1"); B8(n, "t2"); B8(n, "t3"); U32(n, "m0"); B8(n, "t4"); B8(n, "t5");
		U32(n, "m1"); U32(n, "m2"); U32(n, "m3"); B8(n, "t6");
		return Done(n);
	}

	LNode Transition() {
		var n = Begin("transition");
		U32(n, "t1"); U32(n, "t2"); S(n, "s1"); U32(n, "t3"); U32(n, "t4"); S(n, "s2"); U32(n, "t5");
		return Done(n);
	}

	LNode Property() { var n = Begin("property"); S(n, "key"); S(n, "value"); return Done(n); }

	void Events(LNode owner) {
		var list = new List<LNode>();
		while (true) {
			var e = Begin("event");
			int len = RU16(); var name = RBytes(len);
			if (L1.GetString(name) == "events_end") break;
			e.F.Add(new LField { Name = "event", T = 's', V = name });
			S(e, "script");
			list.Add(Done(e));
			if (list.Count > 10000) throw new InvalidDataException("runaway event list");
		}
		owner.F.Add(new LField { Name = "events", T = 'E', V = list });
	}

	LNode Anim() {
		var n = Begin("anim");
		S(n, "name"); B8(n, "a1"); B8(n, "a2");
		L(n, "elements", AnimElement);
		return Done(n);
	}

	LNode AnimElement() {
		var n = Begin("anim_element");
		for (int i = 0; i < 4; i++) U32(n, "k" + i);
		for (int i = 0; i < 4; i++) B8(n, "kb" + i);
		for (int i = 4; i < 11; i++) U32(n, "k" + i);
		U32(n, "e1"); U32(n, "e2");
		L(n, "entries", AnimEntry);
		return Done(n);
	}

	LNode AnimEntry() { var n = Begin("anim_entry"); U32(n, "v"); S(n, "s1"); S(n, "s2"); return Done(n); }

	LNode BlockNode() {
		var n = Begin("block");
		S(n, "type");
		string t = n.Str("type");
		if (t == "List") {
			L(n, "refs", ListRef);
			I32(n, "sx"); I32(n, "sy"); B8(n, "l1"); U32(n, "l2"); B8(n, "l3");
		}
		else if (t == "Table") {
			L(n, "rows", TableRow);
		}
		else throw new InvalidDataException("unknown block type '" + t + "'");
		return Done(n);
	}

	LNode ListRef() { var n = Begin("list_ref"); U32(n, "ref"); return Done(n); }
	LNode TableRow() { var n = Begin("table_row"); L(n, "cells", TableCell); return Done(n); }
	LNode TableCell() { var n = Begin("table_cell"); for (int i = 0; i < 5; i++) U32(n, "c" + i); return Done(n); }

	// ---------- writing ----------
	public static byte[] Write(LNode root) {
		var ms = new MemoryStream();
		var w = new BinaryWriter(ms);
		w.Write(L1.GetBytes(Header));
		WriteNode(w, root);
		w.Flush();
		return ms.ToArray();
	}

	static void WriteNode(BinaryWriter w, LNode n) { foreach (var f in n.F) WriteField(w, f); }

	static void WriteField(BinaryWriter w, LField f) {
		switch (f.T) {
			case 'b': w.Write((byte)f.V); break;
			case 'u': w.Write((uint)f.V); break;
			case 'i': w.Write((int)f.V); break;
			case 's': { var v = (byte[])f.V; w.Write((ushort)v.Length); w.Write(v); break; }
			case 'w': { var v = (byte[])f.V; w.Write((ushort)(v.Length / 2)); w.Write(v); break; }
			case 'L': { var l = (List<LNode>)f.V; w.Write((uint)l.Count); foreach (var x in l) WriteNode(w, x); break; }
			case 'E': {
				foreach (var x in (List<LNode>)f.V) WriteNode(w, x);
				var end = L1.GetBytes("events_end"); w.Write((ushort)end.Length); w.Write(end);
				break;
			}
			case 'B': WriteNode(w, (LNode)f.V); break;
			default: throw new InvalidOperationException("unknown field type " + f.T);
		}
	}

	// ---------- tree helpers ----------
	public static IEnumerable<LNode> Components(LNode root) {
		yield return root;
		foreach (var c in root.List("children")) foreach (var d in Components(c)) yield return d;
	}

	public static LNode FindComponent(LNode root, string name) {
		return Components(root).FirstOrDefault(c => c.Str("name") == name);
	}

	public static LNode ParentOf(LNode root, LNode child) {
		foreach (var c in Components(root)) if (c.List("children").Contains(child)) return c;
		return null;
	}

	// ---------- copying subtrees ----------
	public static LNode DeepClone(LNode n) {
		var c = new LNode { Kind = n.Kind, Offset = n.Offset, End = n.End };
		foreach (var f in n.F) {
			object v = f.V;
			if (f.T == 'L' || f.T == 'E') v = ((List<LNode>)f.V).Select(DeepClone).ToList();
			else if (f.T == 'B') v = DeepClone((LNode)f.V);
			else if (f.T == 's' || f.T == 'w') v = ((byte[])f.V).Clone();
			c.F.Add(new LField { Name = f.Name, T = f.T, V = v });
		}
		return c;
	}

	static IEnumerable<LNode> AllNodes(LNode n) {
		yield return n;
		foreach (var f in n.F) {
			if (f.T == 'L' || f.T == 'E') foreach (var x in (List<LNode>)f.V) foreach (var y in AllNodes(x)) yield return y;
			else if (f.T == 'B') foreach (var y in AllNodes((LNode)f.V)) yield return y;
		}
	}

	// every uid *defined* in the subtree (components, states, images)
	public static HashSet<uint> DefinedUids(LNode n) {
		var s = new HashSet<uint>();
		foreach (var x in AllNodes(n))
			if (x.Kind == "component" || x.Kind == "state" || x.Kind == "image") s.Add(x.U("uid"));
		return s;
	}

	// Give every uid defined in the subtree a fresh value from next(), and rewrite every u32 field in the
	// subtree that holds one of those values (covers known references and any we haven't named).
	// Returns the old->new map.
	public static Dictionary<uint, uint> Reassign(LNode n, Func<uint> next) {
		var map = new Dictionary<uint, uint>();
		foreach (var u in DefinedUids(n)) map[u] = next();
		foreach (var x in AllNodes(n))
			foreach (var f in x.F)
				if (f.T == 'u') { uint nv; if (map.TryGetValue((uint)f.V, out nv)) f.V = nv; }
		return map;
	}

	// all u32 values that look like uids anywhere in a tree (for collision checks)
	public static HashSet<uint> AllU32(LNode n) {
		var s = new HashSet<uint>();
		foreach (var x in AllNodes(n)) foreach (var f in x.F) if (f.T == 'u') s.Add((uint)f.V);
		return s;
	}

	// rename every component named 'from' (exact match) within the subtree
	public static int RenameComponents(LNode n, string from, string to) {
		int k = 0;
		foreach (var c in Components(n)) if (c.Str("name") == from) { c.SetStr("name", to); k++; }
		return k;
	}

	// indented outline: name [class] (x,y) children/states/images
	public static string Outline(LNode n, int depth, int maxDepth) {
		var sb = new StringBuilder();
		sb.AppendFormat("{0}{1} [{2}] ({3},{4}) uid={5:X8} states={6} images={7} children={8}{9}\n",
			new string(' ', depth * 2), n.Str("name"), n.Str("class"), n.I("x"), n.I("y"), n.U("uid"),
			n.List("states").Count, n.List("images").Count, n.List("children").Count,
			n.Block() == null ? "" : " block=" + n.Block().Str("type"));
		if (depth < maxDepth) foreach (var c in n.List("children")) sb.Append(Outline(c, depth + 1, maxDepth));
		return sb.ToString();
	}
}
