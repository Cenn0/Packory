using System.Buffers;
using System.Security.Cryptography;

namespace Packory;

public sealed record CasBlob(string Sha256,long Size,string Path);

// Immutable content-addressed storage. Never trusts user-controlled paths.
public sealed class CasStore
{
    private readonly string _root;
    private readonly string _stage;
    public CasStore(Settings settings)
    {
        var data=Path.GetFullPath(settings.DataDirectory);
        _root=Path.Combine(data,"cas","sha256");
        _stage=Path.Combine(data,"staging");
        Directory.CreateDirectory(_root);Directory.CreateDirectory(_stage);
    }
    public static bool ValidDigest(string digest)=>digest.Length==64 && digest.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public string? Find(string digest)
    {
        if(!ValidDigest(digest))return null;
        var path=Path.Combine(_root,digest[..2],digest[2..4],digest);
        return File.Exists(path)?path:null;
    }
    public async Task<CasBlob> PutAsync(Stream input,long maxBytes,CancellationToken ct,string? expectedDigest=null)
    {
        if(expectedDigest is not null && !ValidDigest(expectedDigest))throw new InvalidDataException("Invalid SHA-256 digest");
        var tmp=Path.Combine(_stage,Guid.NewGuid().ToString("N")+".part");
        long total=0;var buffer=ArrayPool<byte>.Shared.Rent(128*1024);
        try
        {
            using var hasher=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,128*1024,FileOptions.Asynchronous))
            {
                int read;
                while((read=await input.ReadAsync(buffer.AsMemory(),ct))>0)
                {
                    total=checked(total+read);
                    if(total>maxBytes)throw new PayloadTooLargeException();
                    hasher.AppendData(buffer,0,read);
                    await output.WriteAsync(buffer.AsMemory(0,read),ct);
                }
                await output.FlushAsync(ct);
            }
            var digest=Convert.ToHexStringLower(hasher.GetHashAndReset());
            if(expectedDigest is not null && !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),Convert.FromHexString(expectedDigest)))
                throw new InvalidDataException("Content hash does not match declared digest");
            var directory=Path.Combine(_root,digest[..2],digest[2..4]);
            Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,digest);
            try {File.Move(tmp,path);}
            catch(IOException) when(File.Exists(path)) { /* concurrent duplicate payload */ }
            return new CasBlob(digest,total,path);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if(File.Exists(tmp))File.Delete(tmp);
        }
    }

    public int DeleteUnreferenced(Func<string,bool> isReferenced,TimeSpan minAge)
    {
        var removed=0;
        foreach(var file in Directory.EnumerateFiles(_root,"*",SearchOption.AllDirectories))
        {
            var digest=Path.GetFileName(file);
            if(!ValidDigest(digest)||isReferenced(digest))continue;
            try
            {
                if(DateTime.UtcNow-File.GetLastWriteTimeUtc(file)>minAge){File.Delete(file);removed++;}
            }
            catch(IOException){ /* concurrent publish or next reconciliation */ }
        }
        return removed;
    }

    public int DeleteAbandonedStaging(TimeSpan minAge)
    {
        var removed=0;
        foreach(var file in Directory.EnumerateFiles(_stage,"*.part",SearchOption.TopDirectoryOnly))
        {
            try
            {
                if(DateTime.UtcNow-File.GetLastWriteTimeUtc(file)>minAge){File.Delete(file);removed++;}
            }
            catch(IOException){ /* next reconciliation attempt */ }
        }
        return removed;
    }
}
