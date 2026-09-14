using System.Buffers.Binary;
using System.Text;
using H5SoloLauncher.Core;
namespace H5SoloLauncher.Worker;
public sealed record ForgeWindowStatus(bool Visible,bool Fullscreen,float Width,float Height);
public static class ForgePresentation
{
    public static ForgeWindowStatus Run(string root,string package,bool show,CancellationToken cancellation,bool windowed=false)
    {
        var target=PackageTarget.Forge(root,package);var running=target.Find()??throw new CacheException("FORGE_NOT_RUNNING","Forge has closed.");
        using var session=PackageProcessSession.Attach(target,"h5sololauncher.ForgePresentation.dll","H5View",cancellation);
        var bytes=new byte[552];BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x57563548);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),1);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),windowed?2u:show?1u:0u);BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),running.Created);
        var result=session.Call(bytes,cancellation);uint U(int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(offset));
        if(U(12)!=0){var end=Array.IndexOf(result,(byte)0,40,512);throw new CacheException("FORGE_WINDOW_FAILED",Encoding.UTF8.GetString(result,40,(end<0?552:end)-40));}
        return new(U(24)!=0,U(28)!=0,BitConverter.Int32BitsToSingle(unchecked((int)U(32))),BitConverter.Int32BitsToSingle(unchecked((int)U(36))));
    }
}
