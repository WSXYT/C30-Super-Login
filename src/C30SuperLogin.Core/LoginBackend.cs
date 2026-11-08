namespace C30SuperLogin.Core;

// Injectable boundary for deterministic owned-fixture tests; production always uses the Windows backend.
public interface ILoginBackend
{
    IReadOnlyList<TargetProcess> Find(string root, Action<int> queryError);
    bool IsSupported(TargetProcess target);
    Task<NativeResult> InjectAsync(TargetProcess target, string script, CancellationToken token);
    Task<ControlLogin.Result> ControlAsync(TargetProcess target, Credentials credentials, CancellationToken token);
}
internal sealed class WindowsLoginBackend(string helperPath) : ILoginBackend
{
    public IReadOnlyList<TargetProcess> Find(string root, Action<int> queryError) => Target.Find(root, queryError);
    public bool IsSupported(TargetProcess target) => Target.IsSupported(target);
    public Task<NativeResult> InjectAsync(TargetProcess target, string script, CancellationToken token) => new NativeTransport(helperPath).ExecuteAsync(target, script, token);
    public Task<ControlLogin.Result> ControlAsync(TargetProcess target, Credentials credentials, CancellationToken token) => ControlLogin.ExecuteAsync(target, credentials, token);
}
