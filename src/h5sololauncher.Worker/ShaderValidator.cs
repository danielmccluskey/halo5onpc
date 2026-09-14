using System.Runtime.InteropServices;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Conversion;

namespace H5SoloLauncher.Worker;

internal sealed class ShaderValidator : IShaderValidator
{
    private readonly nint library;
    private readonly HashCall hash;
    private readonly ValidateCall validate;
    private readonly CloseCall close;
    private nint device;
    private bool disposed;
    [StructLayout(LayoutKind.Sequential)] private struct Binding { public uint Type, Register, Count, Flags, Return, Dimension, Samples, ConstantBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Validation { public uint Version, Bindings, ConstantBuffers, FeatureLevel; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int HashCall([In] byte[] data, uint size, [Out] byte[] hash);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ValidateCall(nint device,[In] byte[] data, uint size, uint version, [In] Binding[] bindings, uint count, out Validation result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenCall(out nint device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CloseCall(nint device);
    public ShaderValidator()
    {
        library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "h5sololauncher.ShaderTools.dll"));
        try
        {
            hash = Marshal.GetDelegateForFunctionPointer<HashCall>(NativeLibrary.GetExport(library, "H5ShaderHash"));
            validate = Marshal.GetDelegateForFunctionPointer<ValidateCall>(NativeLibrary.GetExport(library, "H5ShaderValidateSession"));
            close=Marshal.GetDelegateForFunctionPointer<CloseCall>(NativeLibrary.GetExport(library,"H5ShaderClose"));
            var open=Marshal.GetDelegateForFunctionPointer<OpenCall>(NativeLibrary.GetExport(library,"H5ShaderOpen"));
            var hr=open(out device);if(hr<0 || device==0)throw new CacheException("SHADER_DEVICE_UNAVAILABLE",$"The graphics driver could not prepare shader validation ({hr:x8}).");
        }
        catch { if(device!=0)close?.Invoke(device);NativeLibrary.Free(library); throw; }
    }
    private byte[] Hash(byte[] program)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var bytes = new byte[16]; var hr = hash(program, (uint)program.Length, bytes);
        if (hr < 0) throw new CacheException("SHADER_CONTAINER_INVALID", $"The shader container checksum could not be calculated ({hr:x8})."); return bytes;
    }
    public void VerifyHash(byte[] program)
    {
        _ = ShaderProgram.Chunks(program);
        if (!Hash(program).AsSpan().SequenceEqual(program.AsSpan(4, 16))) throw new CacheException("SHADER_SOURCE_CHECKSUM", "A shader program doesn't match its source container checksum.");
    }
    public byte[] FinalizeAndValidate(TranslatedShader program)
    {
        var bytes = (byte[])program.Unsigned.Clone(); Hash(bytes).CopyTo(bytes, 4);
        var bindings = program.Bindings.Select(x => new Binding { Type = x.Type, Register = x.Register, Count = x.Count, Flags = x.Flags,
            Return = x.Return, Dimension = x.Dimension, Samples = x.Samples, ConstantBytes = x.ConstantBytes }).ToArray();
        ObjectDisposedException.ThrowIf(disposed,this);
        var hr = validate(device,bytes, (uint)bytes.Length, program.Version, bindings, (uint)bindings.Length, out var result);
        if (hr < 0 || result.Version != program.Version || result.Bindings != bindings.Length)
            throw new CacheException("SHADER_DRIVER_REJECTED", $"The graphics driver or reflection check rejected a converted shader ({hr:x8}). Keep the details for diagnosis.");
        VerifyHash(bytes); return bytes;
    }
    public void Dispose(){if(disposed)return;disposed=true;close(device);device=0;NativeLibrary.Free(library);}
}
