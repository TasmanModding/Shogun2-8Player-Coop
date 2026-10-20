// Writes a Shogun 2 PFH3 "movie" pack (type 4) from in-memory files. The game loads every movie pack in data\
// by itself (no user.script.txt line), so the mod is drag-and-drop.
// Layout: "PFH3", u32 type, u32 ref count, u32 ref index size, u32 file count, u32 index size,
//         8-byte FILETIME, index (u32 size + NUL-terminated path per file), then file data in index order.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class PackWriter {
	public const uint TypeMod = 3;
	public const uint TypeMovie = 4;

	public static void Write(string path, IList<KeyValuePair<string, byte[]>> files) {
		var enc = Encoding.GetEncoding(1252);
		var index = new MemoryStream();
		foreach (var f in files) {
			index.Write(BitConverter.GetBytes((uint)f.Value.Length), 0, 4);
			var name = enc.GetBytes(f.Key.Replace('/', '\\'));
			index.Write(name, 0, name.Length);
			index.WriteByte(0);
		}
		using (var fs = File.Create(path)) {
			var w = new BinaryWriter(fs);
			w.Write(Encoding.ASCII.GetBytes("PFH3"));
			w.Write(TypeMovie);
			w.Write(0u);                                  // pack reference count
			w.Write(0u);                                  // pack reference index size
			w.Write((uint)files.Count);
			w.Write((uint)index.Length);
			// fixed timestamp so every player's pack is byte-identical (multiplayer compares game data)
			w.Write(new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
			w.Write(index.ToArray());
			foreach (var f in files) w.Write(f.Value);
		}
	}
}
