using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace RematchObserver;

/// <summary>
/// ゲームのウィンドウを 1 枚もらう。
///
/// **Windows.Graphics.Capture を使う**（配信ソフトと同じ経路）。
/// ゲームは DirectX で描いているので、**GDI の BitBlt / PrintWindow では黒く出る。**
/// それでも代替を残してあるのは、**WGC が使えない Windows があるから**で、
/// 校正のときに普通のウィンドウを撮るのにも使える。
///
/// ⚠ **注入もメモリ読み取りも入力の送信もしない**（ADR-0067）。
/// ここでやっているのは、外のプロセスが自分の窓を撮るのと同じことである。
/// ⚠ **デスクトップ全体は撮らない。** 撮る相手はウィンドウのハンドル 1 つに限る。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class Capture : IDisposable
{
    private readonly nint _hwnd;
    private IDirect3DDevice? _device;
    private nint _d3dDevice, _d3dContext;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;

    public Capture(nint hwnd) => _hwnd = hwnd;

    public static bool IsSupported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    /// <summary>
    /// 1 枚撮る。**取れなければ null。** 例外にしないのは、
    /// 常駐中に画面が切り替わっただけのことが多いためである。
    /// </summary>
    public async Task<Frame?> GrabAsync(TimeSpan timeout)
    {
        Start();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var frame = _pool!.TryGetNextFrame();
            if (frame is not null) return await ToFrameAsync(frame);
            await Task.Delay(16);
        }
        return null;
    }

    private void Start()
    {
        if (_session is not null) return;
        _device = CreateDevice();
        _item = CreateItemForWindow(_hwnd);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _session = _pool.CreateCaptureSession(_item);
        // ⚠ カーソルも縁取りも要らない。読み取りの邪魔になるので消す
        TrySet(() => _session.IsCursorCaptureEnabled = false);
        if (Windows.Foundation.Metadata.ApiInformation.IsWriteablePropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            TrySet(() => _session.IsBorderRequired = false);
        _session.StartCapture();
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch (Exception) { /* 古い Windows では無い性質がある */ }
    }

    private static async Task<Frame> ToFrameAsync(Direct3D11CaptureFrame frame)
    {
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
            frame.Surface, BitmapAlphaMode.Premultiplied);
        // **表示されている大きさだけを取る。** フレームプールは大きいまま残ることがある
        int w = Math.Min(frame.ContentSize.Width, bitmap.PixelWidth);
        int h = Math.Min(frame.ContentSize.Height, bitmap.PixelHeight);
        return CopyOut(bitmap, w, h);
    }

    private static unsafe Frame CopyOut(SoftwareBitmap bitmap, int width, int height)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        var plane = buffer.GetPlaneDescription(0);
        int stride = width * 4;
        var dst = new byte[stride * height];

        // **CsWinRT の包みは C# の cast では QI できない。**素のポインタまで降りて
        // IMemoryBufferByteAccess を引き、vtable の 3 番（IUnknown の次）を直に呼ぶ。
        // ⚠ ここで得るポインタは buffer を掴んでいるあいだしか有効でない。**その場で写す。**
        nint inspectable = WinRT.MarshalInspectable<object>.FromManaged(reference);
        try
        {
            Check(Marshal.QueryInterface(inspectable, in MemoryBufferByteAccessIid, out nint access),
                  "画像のバッファを引けません");
            try
            {
                var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)
                                (*(void***)access)[3];
                byte* data; uint capacity;
                Check(getBuffer(access, &data, &capacity), "画像のバッファを読めません");
                for (int y = 0; y < height; y++)
                {
                    long src = plane.StartIndex + (long)plane.Stride * y;
                    if (src + stride > capacity)
                        throw new InvalidOperationException("キャプチャの範囲が合いません");
                    Marshal.Copy((nint)(data + src), dst, y * stride, stride);
                }
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(inspectable); }
        return new Frame(width, height, stride, dst);
    }

    // ---- WinRT との継ぎ目 ------------------------------------------------
    // **ここだけが素の COM である。** 上の層は Frame しか触らない。

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid CaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid MemoryBufferByteAccessIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(
        nint adapter, int driverType, nint software, uint flags, nint featureLevels,
        uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Check(WindowsCreateString(className, className.Length, out nint hstring), "クラス名を作れません");
        nint factoryPtr = 0;
        try
        {
            Check(RoGetActivationFactory(hstring, in CaptureItemInteropIid, out factoryPtr),
                  "画面キャプチャがこの Windows で使えません");
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            nint itemPtr = interop.CreateForWindow(hwnd, in GraphicsCaptureItemIid);
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally
        {
            if (factoryPtr != 0) Marshal.Release(factoryPtr);
            WindowsDeleteString(hstring);
        }
    }

    private IDirect3DDevice CreateDevice()
    {
        const int hardware = 1;
        const uint bgraSupport = 0x20;
        const uint sdkVersion = 7;
        Check(D3D11CreateDevice(0, hardware, 0, bgraSupport, 0, 0, sdkVersion,
                                out _d3dDevice, out _, out _d3dContext),
              "Direct3D の装置を作れません");
        nint dxgi = 0, inspectable = 0;
        try
        {
            Check(Marshal.QueryInterface(_d3dDevice, in DxgiDeviceIid, out dxgi), "DXGI が引けません");
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable), "WinRT の装置を作れません");
            return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what}（0x{hr:X8}）");
    }

    public void Dispose()
    {
        _session?.Dispose(); _session = null;
        _pool?.Dispose(); _pool = null;
        _item = null;
        if (_device is IDisposable d) d.Dispose();
        _device = null;
        if (_d3dContext != 0) { Marshal.Release(_d3dContext); _d3dContext = 0; }
        if (_d3dDevice != 0) { Marshal.Release(_d3dDevice); _d3dDevice = 0; }
    }
}

/// <summary>
/// GDI での代替。**ゲームには効かない見込み**（DirectX の描画は取れない）が、
/// **WGC が無い Windows と、校正のときの普通のウィンドウ**のために残す。
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class PrintWindowCapture
{
    private const uint RenderFullContent = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static Frame? Grab(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            nint hdc = g.GetHdc();
            try { if (!PrintWindow(hwnd, hdc, RenderFullContent)) return null; }
            finally { g.ReleaseHdc(hdc); }
        }
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = w * 4;
            var buf = new byte[stride * h];
            for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * stride, stride);
            return new Frame(w, h, stride, buf);
        }
        finally { bmp.UnlockBits(data); }
    }
}
