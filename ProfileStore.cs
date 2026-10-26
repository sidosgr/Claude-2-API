using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ClaudeLocalBridge;

public sealed record AccountProfile(string Id,string Name,bool Enabled=true,DateTimeOffset? LimitedUntil=null)
{
    public string Label=>Name+(!Enabled?" · disabled":LimitedUntil>DateTimeOffset.UtcNow?" · paused until "+LimitedUntil.Value.ToLocalTime().ToString("HH:mm"):" · available");
}
public sealed class ProfileStore
{
    private readonly string path;
    private List<AccountProfile> profiles;
    public string ActiveId{get;private set;}
    public IReadOnlyList<AccountProfile> Profiles=>profiles.ToArray();
    public ProfileStore(string? storagePath=null)
    {
        path=storagePath??Path.Combine(BridgeState.DataFolder,"profiles.json");
        var saved=File.Exists(path)?JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)):null;
        profiles=saved?.Profiles??[new("default","Default")];ActiveId=saved?.ActiveId??"default";
        if(profiles.Count is <1 or >20||profiles.Any(p=>!ValidId(p.Id)||!ValidName(p.Name))||profiles.Select(p=>p.Id).Distinct().Count()!=profiles.Count||profiles.Select(p=>p.Name.ToUpperInvariant()).Distinct().Count()!=profiles.Count||!profiles.Any(p=>p.Id==ActiveId))throw new InvalidDataException("Invalid saved profiles.");
    }
    public static bool ValidId(string id)=>id=="default"||Regex.IsMatch(id,@"^[a-f0-9]{32}$");
    private static bool ValidName(string name)=>name.Length is >=1 and <=60&&!name.Any(char.IsControl)&&name==name.Trim();
    public AccountProfile Add(string name)
    {
        if(profiles.Count>=20)throw new ArgumentException("Up to 20 profiles can be saved.");
        ValidateName(name=name.Trim(),null);var profile=new AccountProfile(Guid.NewGuid().ToString("N"),name);
        Change(()=>profiles.Add(profile));return profile;
    }
    public void Rename(string id,string name){ValidateName(name=name.Trim(),id);Replace(id,p=>p with{Name=name});}
    public void Enable(string id,bool enabled)=>Replace(id,p=>p with{Enabled=enabled});
    public void Reset(string id)=>Replace(id,p=>p with{LimitedUntil=null});
    public void Limit(string id,DateTimeOffset until)=>Replace(id,p=>p with{LimitedUntil=until});
    public void Activate(string id)
    {
        if(!profiles.Any(p=>p.Id==id))throw new ArgumentException("Unknown profile.");
        if(ActiveId==id&&File.Exists(path))return;
        Change(()=>ActiveId=id);
    }
    public void Move(string id,int offset)
    {
        var index=profiles.FindIndex(p=>p.Id==id);if(index<0)throw new ArgumentException("Unknown profile.");
        var target=Math.Clamp(index+offset,0,profiles.Count-1);
        Change(()=>{var item=profiles[index];profiles.RemoveAt(index);profiles.Insert(target,item);});
    }
    public string BrowserFolder(string id)
    {
        if(!ValidId(id)||!profiles.Any(p=>p.Id==id))throw new ArgumentException("Unknown profile.");
        var root=Path.GetDirectoryName(Path.GetFullPath(path))!;
        return id=="default"?Path.Combine(root,"BrowserProfile"):Path.Combine(root,"Profiles",id,"BrowserProfile");
    }
    private void ValidateName(string name,string? id)
    {
        if(!ValidName(name))throw new ArgumentException("Use a profile name with 1–60 printable characters.");
        if(profiles.Any(p=>p.Id!=id&&p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Choose a unique profile name.");
    }
    private void Replace(string id,Func<AccountProfile,AccountProfile> transform)
    {
        var index=profiles.FindIndex(p=>p.Id==id);if(index<0)throw new ArgumentException("Unknown profile.");
        Change(()=>profiles[index]=transform(profiles[index]));
    }
    private void Change(Action action)
    {
        var before=profiles.ToList();var active=ActiveId;
        try{action();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new Saved(ActiveId,profiles),new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);}
        catch{profiles=before;ActiveId=active;throw;}
    }
    private sealed record Saved(string ActiveId,List<AccountProfile> Profiles);
}

public sealed class ProfileRouter(ProfileStore store)
{
    public async Task<Completion> CompleteAsync(bool failover,int cooldownMinutes,Func<AccountProfile,CancellationToken,Task<bool>> prepare,Func<AccountProfile,CancellationToken,Task<Completion>> complete,Action<AccountProfile>? limited,CancellationToken cancellation)
    {
        var all=store.Profiles;var active=all.ToList().FindIndex(p=>p.Id==store.ActiveId);
        var ordered=Enumerable.Range(0,all.Count).Select(i=>all[(active+i)%all.Count]).ToArray();
        foreach(var profile in failover?ordered:ordered.Take(1))
        {
            cancellation.ThrowIfCancellationRequested();
            if(!profile.Enabled||profile.LimitedUntil>DateTimeOffset.UtcNow)continue;
            if(!await prepare(profile,cancellation))continue;
            store.Activate(profile.Id);
            try{return await complete(profile,cancellation);}
            catch(BridgeException ex)when(ex.Status==429&&ex.Code=="claude_limit")
            {
                store.Limit(profile.Id,DateTimeOffset.UtcNow.AddMinutes(cooldownMinutes));limited?.Invoke(profile);
                if(!failover)throw;
            }
        }
        throw new BridgeException(429,"profiles_unavailable","No enabled, signed-in profile is available for this model. Profiles at a usage limit are paused until their cooldown ends. Check Profiles settings or reset a pause after Claude's allowance resets.");
    }
}
