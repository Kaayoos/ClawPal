using System.Buffers.Binary;
using System.Text;

namespace ClawPal.Companion;

// ESP-IDF NVS v2: one active page with string entries, five erased pages.
// Matches Espressif's documented on-flash layout; tested against its generator.
public static class NvsImage
{
    public static byte[] Create(IReadOnlyDictionary<string, string> values)
    {
        var image = Enumerable.Repeat((byte)255, 0x6000).ToArray();
        Write32(image, 0, 0xfffffffe); Write32(image, 4, 0); image[8] = 0xfe;
        Write32(image, 28, Crc(image.AsSpan(4, 24)));
        int index = 0;
        byte[] Header(string key, byte ns, byte type, int span)
        {
            if (Encoding.UTF8.GetByteCount(key) > 15 || key.Contains('\0')) throw new ArgumentException(L.Text("invalid_setting_key"));
            var h = Enumerable.Repeat((byte)255, 32).ToArray();
            h[0] = ns; h[1] = type; h[2] = checked((byte)span);
            Array.Clear(h, 8, 16); Encoding.UTF8.GetBytes(key).CopyTo(h, 8); return h;
        }
        void Append(byte[] header, byte[]? data = null)
        {
            int span = header[2];
            if (index + span > 126) throw new ArgumentException(L.Text("device_settings_are_too_large"));
            Write32(header, 4, Crc(header.Take(4).Concat(header.Skip(8)).ToArray()));
            header.CopyTo(image, 64 + index * 32);
            data?.CopyTo(image, 96 + index * 32);
            for (int i = 0; i < span; i++, index++) image[32 + index / 4] &= (byte)~(1 << (index % 4 * 2));
        }
        var ns = Header("buddy", 0, 1, 1); ns[24] = 1; Append(ns);
        foreach (var (key, value) in values)
        {
            if (value.Contains('\0')) throw new ArgumentException(L.Text("settings_cannot_contain_a_null_character"));
            var data = Encoding.UTF8.GetBytes(value + '\0');
            var h = Header(key, 1, 0x21, 1 + (data.Length + 31) / 32);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(24), checked((ushort)data.Length));
            Write32(h, 28, Crc(data)); Append(h, data);
        }
        return image;
    }
    // zlib.crc32(data, 0xffffffff), as used by ESP-IDF.
    public static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0;
        foreach (byte b in bytes) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0); }
        return ~crc;
    }
    private static void Write32(byte[] target, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), value);
}
