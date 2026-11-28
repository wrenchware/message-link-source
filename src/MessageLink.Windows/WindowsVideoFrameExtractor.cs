using System.Runtime.InteropServices;
using MessageLink.Core.Services;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace MessageLink.WindowsPlatform;

// One frame of a video, made by the same player that plays it. A MediaPlayer opens the video from memory
// (MediaSource.CreateFromStream over an in-memory stream) in frame-server mode, which hands the decoded picture to the
// caller instead of drawing it: no window, no file, and whatever codecs Windows can play are the codecs this can read.
// Nothing is written to disk, neither the video nor the frame.
//
// Frame-server mode copies the frame into a Direct3D surface (CopyFrameToVideoSurface), and Windows has no managed way
// to make one without a graphics library. The surface is made here with three calls into d3d11.dll, on a hardware device
// (WARP when there is no GPU), and handed to the player as the WinRT surface it expects. Everything runs on the thread
// pool: the player raises its events there and needs neither the interface thread nor a window.
public sealed class WindowsVideoFrameExtractor : IVideoFrameExtractor
{
    // A clip that has not produced a frame by now is not going to, and the tile keeps its play button.
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    // Far enough in to be past a fade-in or a black first frame, never beyond a short clip's tenth.
    internal static readonly TimeSpan IntoTheClip = TimeSpan.FromMilliseconds(500);
    private const float Quality = 0.85f;

    public async Task<byte[]?> ExtractJpegAsync(byte[] video, string mimeType, int longEdge, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Bound);
        try { return await Task.Run(() => ExtractAsync(video, mimeType, longEdge, limit.Token), CancellationToken.None); }
        // The port does not throw for a clip it cannot read or a wait that ran out; only the caller's own cancellation.
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static async Task<byte[]?> ExtractAsync(byte[] video, string mimeType, int longEdge, CancellationToken token)
    {
        using var memory = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memory.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(video);
            await writer.StoreAsync().AsTask(token);
            writer.DetachStream(); // Disposing the writer would close the stream with it.
        }
        memory.Seek(0);
        using var source = MediaSource.CreateFromStream(memory, string.IsNullOrWhiteSpace(mimeType) ? "video/mp4" : mimeType);
        using var player = new MediaPlayer { IsVideoFrameServerEnabled = true, IsMuted = true, AutoPlay = false, Source = source };
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // False when the player failed after opening: no frame came, and the surface is still black (a black still was cached for good, 2026-11-18).
        var framed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wanted = 0; // Frames are taken only once the seek has landed, never the one the player shows first.
        D3dSurface? surface = null;
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, _) => { opened.TrySetResult(false); seeked.TrySetResult(); framed.TrySetResult(false); };
        player.PlaybackSession.SeekCompleted += (_, _) => seeked.TrySetResult();
        player.VideoFrameAvailable += (sender, _) =>
        {
            if (Volatile.Read(ref wanted) == 0 || surface is null || framed.Task.IsCompleted) return;
            try { sender.CopyFrameToVideoSurface(surface.Surface); }
            catch (Exception) { return; } // The next frame may be copied.
            framed.TrySetResult(true);
        };
        using var registration = token.Register(() => { opened.TrySetCanceled(token); framed.TrySetCanceled(token); seeked.TrySetCanceled(token); });

        if (!await opened.Task) return null;
        var session = player.PlaybackSession;
        int width = (int)session.NaturalVideoWidth, height = (int)session.NaturalVideoHeight;
        if (width <= 0 || height <= 0) return null;
        var scale = Math.Min(1d, (double)longEdge / Math.Max(width, height));
        int surfaceWidth = Math.Max(2, (int)Math.Round(width * scale)), surfaceHeight = Math.Max(2, (int)Math.Round(height * scale));
        surface = D3dSurface.Create(surfaceWidth, surfaceHeight);
        try
        {
            player.SetSurfaceSize(new Size(surfaceWidth, surfaceHeight));
            var length = session.NaturalDuration;
            var target = length > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Min(IntoTheClip.Ticks, length.Ticks / 10)) : TimeSpan.Zero;
            if (target > TimeSpan.Zero)
            {
                session.Position = target;
                await seeked.Task;
            }
            Volatile.Write(ref wanted, 1);
            player.Play();
            var framedOne = await framed.Task;
            player.Pause();
            if (!framedOne) return null;
            using var picture = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface.Surface).AsTask(token);
            using var opaque = SoftwareBitmap.Convert(picture, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            return await EncodeAsync(opaque, longEdge, token);
        }
        finally { surface.Dispose(); }
    }

    private static async Task<byte[]> EncodeAsync(SoftwareBitmap bitmap, int longEdge, CancellationToken token)
    {
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encoded,
            [new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(Quality, PropertyType.Single))]).AsTask(token);
        encoder.SetSoftwareBitmap(bitmap);
        var current = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (longEdge < current)
        {
            var scale = (double)longEdge / current;
            encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(bitmap.PixelWidth * scale));
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(bitmap.PixelHeight * scale));
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        await encoder.FlushAsync().AsTask(token);
        var bytes = new byte[checked((int)encoded.Size)];
        using var reader = new DataReader(encoded.GetInputStreamAt(0));
        await reader.LoadAsync((uint)bytes.Length).AsTask(token);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // A BGRA render-target texture on its own Direct3D 11 device, and the WinRT surface the player copies a frame into.
    private sealed class D3dSurface : IDisposable
    {
        private IntPtr device, context, texture, dxgiSurface, winrtSurface;
        public IDirect3DSurface Surface { get; private set; } = null!;

        public static D3dSurface Create(int width, int height)
        {
            var made = new D3dSurface();
            try { made.Build(width, height); return made; }
            catch (Exception) { made.Dispose(); throw; }
        }

        private void Build(int width, int height)
        {
            const uint HardwareDriver = 1, WarpDriver = 5, BgraSupport = 0x20, SdkVersion = 7;
            var hr = D3D11CreateDevice(IntPtr.Zero, HardwareDriver, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion, out device, out _, out context);
            if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, WarpDriver, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion, out device, out _, out context);
            Marshal.ThrowExceptionForHR(hr);
            var description = new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 87 /* B8G8R8A8_UNORM */,
                SampleCount = 1, SampleQuality = 0, Usage = 0, BindFlags = 0x8 | 0x20 /* shader resource, render target */,
            };
            // ID3D11Device::CreateTexture2D is the sixth slot of its vtable, after IUnknown's three, CreateBuffer and CreateTexture1D.
            var table = Marshal.ReadIntPtr(device);
            var create = Marshal.GetDelegateForFunctionPointer<CreateTexture2D>(Marshal.ReadIntPtr(table, 5 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(create(device, ref description, IntPtr.Zero, out texture));
            var dxgi = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec"); // IDXGISurface
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, in dxgi, out dxgiSurface));
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out winrtSurface));
            Surface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(winrtSurface);
        }

        public void Dispose()
        {
            (Surface as IDisposable)?.Dispose();
            foreach (var pointer in new[] { winrtSurface, dxgiSurface, texture, context, device })
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            winrtSurface = dxgiSurface = texture = context = device = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2D(IntPtr device, ref Texture2DDescription description, IntPtr initialData, out IntPtr texture);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);
}
