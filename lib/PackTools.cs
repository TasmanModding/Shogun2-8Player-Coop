// Reader for Shogun 2 PFH3 .pack archives (uncompressed). Load from PowerShell with:
//   Add-Type -Path "re\scripts\PackTools.cs"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

public class PackEntry { public string Name; public long Offset; public uint Size; }

public static class Pack {
	static readonly Encoding Enc = Encoding.GetEncoding(1252);

	public static List<PackEntry> Index(string pack) {
		var res = new List<PackEntry>();
		using (var fs = File.OpenRead(pack)) {
			var br = new BinaryReader(fs);
			string magic = Encoding.ASCII.GetString(br.ReadBytes(4)); uint type = br.ReadUInt32();
			if (magic != "PFH3" || (type & ~0x4Fu) != 0) throw new Exception("unsupported pack " + magic + " type " + type);
			// flag 0x40: every index entry carries an 8-byte FILETIME after its size
			bool stamps = (type & 0x40) != 0;
			br.ReadUInt32(); uint refsz = br.ReadUInt32(); uint n = br.ReadUInt32(); uint idx = br.ReadUInt32();
			br.ReadBytes(8); br.ReadBytes((int)refsz);
			byte[] raw = br.ReadBytes((int)idx); long pos = fs.Position; int p = 0;
			for (int i = 0; i < n; i++) {
				uint size = BitConverter.ToUInt32(raw, p); p += 4;
				if (stamps) p += 8;
				int e = Array.IndexOf(raw, (byte)0, p);
				res.Add(new PackEntry { Name = Enc.GetString(raw, p, e - p), Offset = pos, Size = size });
				p = e + 1; pos += size;
			}
		}
		return res;
	}

	public static byte[] Read(string pack, PackEntry e) {
		using (var fs = File.OpenRead(pack)) { fs.Position = e.Offset; var b = new byte[e.Size]; fs.Read(b, 0, b.Length); return b; }
	}

	public static List<string> Extract(string pack, string outDir, string regex) {
		var rx = new Regex(regex, RegexOptions.IgnoreCase); var res = new List<string>();
		foreach (var e in Index(pack)) {
			if (!rx.IsMatch(e.Name)) continue;
			string o = Path.Combine(outDir, e.Name);
			Directory.CreateDirectory(Path.GetDirectoryName(o));
			File.WriteAllBytes(o, Read(pack, e));
			res.Add(e.Name + " (" + e.Size + ")");
		}
		return res;
	}

	// stream every file out of the pack into outDir; returns file count, logs progress to the console
	public static int ExtractAll(string pack, string outDir) {
		var entries = Index(pack); var buf = new byte[1 << 20]; int done = 0; long bytes = 0;
		using (var fs = File.OpenRead(pack)) {
			foreach (var e in entries) {
				string o = Path.Combine(outDir, e.Name);
				Directory.CreateDirectory(Path.GetDirectoryName(o));
				fs.Position = e.Offset;
				using (var w = File.Create(o)) {
					long left = e.Size;
					while (left > 0) {
						int r = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
						if (r <= 0) throw new Exception("unexpected end of pack reading " + e.Name);
						w.Write(buf, 0, r); left -= r;
					}
				}
				done++; bytes += e.Size;
				if (done % 5000 == 0) Console.WriteLine("  {0}: {1}/{2} files, {3:N0} MB", Path.GetFileName(pack), done, entries.Count, bytes >> 20);
			}
		}
		return done;
	}

	// files (optionally filtered by name regex) whose bytes contain the ASCII text
	public static List<string> Grep(string pack, string text, string nameRegex) {
		var rx = nameRegex == null ? null : new Regex(nameRegex, RegexOptions.IgnoreCase);
		byte[] k = Enc.GetBytes(text); var res = new List<string>();
		using (var fs = File.OpenRead(pack)) {
			foreach (var e in Index(pack)) {
				if (rx != null && !rx.IsMatch(e.Name)) continue;
				if (e.Size > 64 * 1024 * 1024) continue;
				fs.Position = e.Offset; var b = new byte[e.Size]; fs.Read(b, 0, b.Length);
				for (int i = 0; i + k.Length <= b.Length; i++) {
					int j = 0; while (j < k.Length && b[i + j] == k[j]) j++;
					if (j == k.Length) { res.Add(e.Name); break; }
				}
			}
		}
		return res;
	}
}
