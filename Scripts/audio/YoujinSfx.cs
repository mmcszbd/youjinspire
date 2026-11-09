// ============================================================================
// 友尽尖塔（youjin）：音效播放（右键 Q弹 / 拖动相撞）
// ----------------------------------------------------------------------------
// ★ 音频文件放 mod 的 pck 里，**按原始字节读**，不走 ResourceLoader：
//   实测 `FileAccess.file_exists = true` 但 `ResourceLoader.exists = false`
//   （pck 里没有 .godot/imported 的导入产物），所以"读字节 → 自己塞进 AudioStream"
//   是唯一正确的打开方式。BGM 那首 `fmod_audio/yuai.mp3` 就是这么做的，这里沿用。
//
// ★ 三种格式都支持（你导出 pck 时放哪个都行，按下面顺序找第一个存在的）：
//     1. res://fmod_audio/twangy_boing.ogg   → AudioStreamOggVorbis.LoadFromBuffer(bytes)
//     2. res://fmod_audio/twangy_boing.mp3   → new AudioStreamMP3 { Data = bytes }
//     3. res://fmod_audio/twangy_boing.wav   → ⚠ 必须自己解析 RIFF 头
//        （AudioStreamWav.Data 要的是**裸 PCM 采样**，直接塞整个 wav 文件会放不出声/爆音）
//
// ★ 每次播放**新建一个 AudioStreamPlayer**，挂在 SceneTree.Root 下、播完自己 QueueFree：
//   · 一帧里可能同时有 2~3 个碰撞触发，共用一个播放器会互相打断
//   · 挂 Root 而不是战斗房间 —— 房间销毁不该把正在响的一声掐掉
//   · 加一点随机音高（PitchScale），连续触发才不呆板
// ============================================================================

using System;
using Godot;

namespace YouJin.Scripts;

/// <summary>右键 Q弹 / 拖动相撞的音效。</summary>
internal static class YoujinSfx
{
    /// <summary>音效文件名（不含扩展名）。pck 里必须放在这个位置。</summary>
    private const string BoingBase = "res://fmod_audio/twangy_boing";

    /// <summary>按顺序尝试的扩展名。</summary>
    private static readonly string[] Extensions = { ".ogg", ".mp3", ".wav" };

    /// <summary>相撞受击的音量（dB）。</summary>
    private const float HitVolumeDb = -8f;

    private static AudioStream? _boing;
    private static bool _triedLoad;

    /// <summary>相撞受击（两端各播各的）。</summary>
    internal static void PlayHit()
        => PlayBoing(HitVolumeDb, pitchMin: 0.86f, pitchMax: 1.18f);

    private static void PlayBoing(float volumeDb, float pitchMin, float pitchMax)
    {
        var stream = LoadBoing();
        if (stream is null)
            return;

        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return;

            var player = new AudioStreamPlayer
            {
                Stream = stream,
                VolumeDb = volumeDb,
                PitchScale = pitchMin + GD.Randf() * (pitchMax - pitchMin),
            };

            // 播完自己销毁 —— 一帧里可能同时有好几个，不能共用一个播放器。
            player.Finished += player.QueueFree;

            tree.Root.AddChild(player);

            try
            {
                player.Play();
            }
            catch
            {
                player.QueueFree();
                throw;
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"音效：播放失败（忽略）{ex}");
        }
    }

    /// <summary>加载（只读一次，之后复用同一个 AudioStream）。</summary>
    private static AudioStream? LoadBoing()
    {
        if (_triedLoad)
            return _boing;

        _triedLoad = true;

        // ① 先按约定名字找（最快，不依赖目录遍历）
        foreach (var ext in Extensions)
        {
            if (TryLoadPath(BoingBase + ext) is { } stream)
                return _boing = stream;
        }

        // ② 兜底：扫 fmod_audio 目录，找名字里带 twangy / boing 的音频文件。
        //    ★ 为什么要这一步：实际放进来的文件叫
        //      "Twangy Boing Sound Buttons.mp3"（带空格、大写），
        //      硬编码路径必然对不上。文件名怎么起都行，含这两个关键词之一即可。
        foreach (var path in FindByKeyword())
        {
            if (TryLoadPath(path) is { } stream)
                return _boing = stream;
        }

        Entry.Logger.Info(
            "音效：pck 里没有找到 boing 音效。期望 res://fmod_audio/twangy_boing(.ogg/.mp3/.wav)，"
            + "或 fmod_audio 目录里任何名字含 twangy/boing 的 .ogg/.mp3/.wav");
        return null;
    }

    /// <summary>试着加载一个具体路径；成功返回 stream，失败返回 null 并打日志。</summary>
    private static AudioStream? TryLoadPath(string path)
    {
        try
        {
            if (!Godot.FileAccess.FileExists(path))
                return null;

            var bytes = Godot.FileAccess.GetFileAsBytes(path);
            if (bytes is null || bytes.Length == 0)
            {
                Entry.Logger.Info($"音效：{path} 读出来是空的");
                return null;
            }

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var stream = BuildStream(bytes, ext);

            if (stream is null)
            {
                Entry.Logger.Info($"音效：{path} 解析失败（{ext}）");
                return null;
            }

            Entry.Logger.Info($"音效：已加载 {path}（{bytes.Length} 字节，{ext}）");
            return stream;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"音效：加载 {path} 出错 {ex}");
            return null;
        }
    }

    /// <summary>扫 pck 里的 fmod_audio 目录，挑出名字含关键字、扩展名受支持的音频。</summary>
    private static System.Collections.Generic.List<string> FindByKeyword()
    {
        var found = new System.Collections.Generic.List<string>();
        var listed = new System.Collections.Generic.List<string>();

        try
        {
            var dir = DirAccess.Open("res://fmod_audio");
            if (dir is null)
            {
                Entry.Logger.Info("音效：DirAccess 打不开 res://fmod_audio");
                return found;
            }

            foreach (var name in dir.GetFiles())
            {
                listed.Add(name);

                var lower = name.ToLowerInvariant();

                // `.import` 之类的副产物会被 pck 一起带进来，靠扩展名过滤掉
                var ext = System.IO.Path.GetExtension(lower);
                if (Array.IndexOf(Extensions, ext) < 0)
                    continue;

                if (!lower.Contains("twangy") && !lower.Contains("boing"))
                    continue;

                found.Add("res://fmod_audio/" + name);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"音效：枚举 fmod_audio 目录失败 {ex}");
        }

        // ★ 没找到时把目录里**实际有什么**打出来 ——
        //   这次的教训是：文件名带了空格和大写（"Twangy Boing Sound Buttons.mp3"），
        //   只报一句"没找到"根本没法定位。这里直接把清单摊开。
        if (found.Count == 0)
        {
            var sample = listed.Count == 0
                ? "（空）"
                : string.Join(" / ", listed.GetRange(0, Math.Min(8, listed.Count)))
                  + (listed.Count > 8 ? $" …（共 {listed.Count} 个）" : "");

            Entry.Logger.Info($"音效：fmod_audio 目录内容 = {sample}");
        }

        return found;
    }

    private static AudioStream? BuildStream(byte[] bytes, string ext) => ext switch
    {
        ".ogg" => AudioStreamOggVorbis.LoadFromBuffer(bytes),
        ".mp3" => new AudioStreamMP3 { Data = bytes, Loop = false },
        ".wav" => BuildWavStream(bytes),
        _ => null,
    };

    /// <summary>
    /// 最小 RIFF/WAVE 解析：只取 `fmt ` 和 `data` 两个块，支持 8/16 位 PCM。
    ///
    /// ⚠ 关键点：`AudioStreamWav.Data` 要的是**没有头的裸 PCM**，
    ///   直接把整个 .wav 文件塞进去会变成了噪声 / 放不出来。
    /// </summary>
    private static AudioStreamWav? BuildWavStream(byte[] bytes)
    {
        if (bytes.Length < 44)
            return null;

        if (bytes[0] != (byte)'R' || bytes[1] != (byte)'I'
            || bytes[2] != (byte)'F' || bytes[3] != (byte)'F')
            return null;

        var channels = 1;
        var rate = 44100;
        var bits = 16;
        var dataOffset = -1;
        var dataLength = 0;

        // "RIFF" + size(4) + "WAVE" → 第一个块从 12 开始
        var i = 12;

        while (i + 8 <= bytes.Length)
        {
            var id = new string(new[]
            {
                (char)bytes[i], (char)bytes[i + 1], (char)bytes[i + 2], (char)bytes[i + 3],
            });

            var size = BitConverter.ToInt32(bytes, i + 4);
            var body = i + 8;

            if (size < 0 || body > bytes.Length)
                break;

            if (id == "fmt " && body + 16 <= bytes.Length)
            {
                channels = BitConverter.ToUInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToUInt16(bytes, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = Math.Min(size, bytes.Length - body);
                break;
            }

            // 块按字对齐：奇数长度要跳过 1 字节填充
            i = body + size + (size % 2);
        }

        if (dataOffset < 0 || dataLength <= 0 || rate <= 0)
            return null;

        var pcm = new byte[dataLength];
        Array.Copy(bytes, dataOffset, pcm, 0, dataLength);

        return new AudioStreamWav
        {
            Data = pcm,
            MixRate = rate,
            Stereo = channels >= 2,
            Format = bits == 8
                ? AudioStreamWav.FormatEnum.Format8Bits
                : AudioStreamWav.FormatEnum.Format16Bits,
        };
    }
}
