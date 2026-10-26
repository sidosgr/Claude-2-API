using ClaudeLocalBridge;
internal static class ProfileTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"BridgeProfiles-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(root,"profiles.json");
        var store=new ProfileStore(path);
        check(store.ActiveId=="default"&&store.Profiles.Count==1,"Existing login must map to Default");
        check(store.BrowserFolder("default")==Path.Combine(root,"BrowserProfile"),"Default browser data must not move");
        var work=store.Add("Work");var backup=store.Add("Backup");
        check(store.BrowserFolder(work.Id)!=store.BrowserFolder(backup.Id)&&store.BrowserFolder(work.Id).StartsWith(Path.Combine(root,"Profiles")),"Named sessions must have isolated browser folders");
        store.Rename(work.Id,"Personal account");store.Activate(work.Id);
        var restored=new ProfileStore(path);
        check(restored.ActiveId==work.Id&&restored.Profiles[1].Name=="Personal account","Names and active profile must persist");
        try{store.Add("PERSONAL ACCOUNT");throw new Exception("Duplicate profile name accepted");}catch(ArgumentException){check(true,"Duplicate names rejected");}
        try{store.BrowserFolder("../escape");throw new Exception("Unsafe profile ID accepted");}catch(ArgumentException){check(true,"Unsafe profile ID rejected");}
        store.Move(backup.Id,-2);check(new ProfileStore(path).Profiles[0].Id==backup.Id,"Profile order must persist");
        store.Move(backup.Id,2);store.Activate("default");
        var router=new ProfileRouter(store);var calls=new List<string>();
        var answer=await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>
        {
            calls.Add(profile.Id);
            if(profile.Id=="default")throw new BridgeException(429,"claude_limit","limited");
            return Task.FromResult(new Completion("OK","claude-sonnet-5-5"));
        },null,CancellationToken.None);
        check(answer.Text=="OK"&&calls.SequenceEqual(new[]{"default",work.Id}),"A quota response must try the next profile once");
        check(store.ActiveId==work.Id&&new ProfileStore(path).ActiveId==work.Id,"Successful failover must remain active across restart");
        check(new ProfileStore(path).Profiles.First(p=>p.Id=="default").LimitedUntil>DateTimeOffset.UtcNow,"Quota cooldown must persist");
        calls.Clear();
        await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);return Task.FromResult(new Completion("OK","test"));},null,CancellationToken.None);
        check(calls.SequenceEqual(new[]{work.Id}),"Healthy active profile should be reused");
        calls.Clear();
        try{await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);throw new BridgeException(502,"bad_response","bad");},null,CancellationToken.None);throw new Exception("Expected upstream error");}
        catch(BridgeException ex){check(ex.Status==502&&calls.Count==1,"Non-quota generation errors must not rotate");}
        calls.Clear();
        try{await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);throw new BridgeException(503,"session_expired","expired");},null,CancellationToken.None);throw new Exception("Expected auth error");}
        catch(BridgeException ex){check(ex.Code=="session_expired"&&calls.Count==1,"Authentication errors must not rotate");}
        store.Enable(backup.Id,false);calls.Clear();
        try{await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);throw new BridgeException(429,"claude_limit","limit");},null,CancellationToken.None);throw new Exception("Expected exhaustion");}
        catch(BridgeException ex){check(ex.Code=="profiles_unavailable"&&calls.SequenceEqual(new[]{work.Id}),"Disabled and cooling profiles must be skipped, without loops");}
        store.Reset("default");store.Reset(work.Id);store.Activate("default");calls.Clear();
        try{await router.CompleteAsync(false,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);throw new BridgeException(429,"claude_limit","limit");},null,CancellationToken.None);throw new Exception("Expected single limit");}
        catch(BridgeException ex){check(ex.Code=="claude_limit"&&calls.SequenceEqual(new[]{"default"}),"Failover-off must keep the original quota response");}
        store.Reset("default");store.Reset(work.Id);store.Enable(backup.Id,true);store.Activate("default");calls.Clear();
        await router.CompleteAsync(true,60,(profile,_)=>Task.FromResult(profile.Id!=work.Id),(profile,_)=>
        {calls.Add(profile.Id);if(profile.Id=="default")throw new BridgeException(429,"claude_limit","limit");return Task.FromResult(new Completion("OK","test"));},null,CancellationToken.None);
        check(calls.SequenceEqual(new[]{"default",backup.Id}),"A signed-out fallback should be skipped");
        store.Reset("default");store.Activate("default");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(_,_)=>throw new Exception("Cancelled request sent"),null,cancel.Token);throw new Exception("Expected cancellation");}
        catch(OperationCanceledException){check(true,"Cancelled request must not be sent");}
        check(!File.ReadAllText(path).Contains("sessionKey"),"Profile metadata must not store cookies");
    }
}
