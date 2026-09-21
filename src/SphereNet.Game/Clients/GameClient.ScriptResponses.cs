using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    private IScriptObj? _pendingMenuSubject;
    private List<ScriptKey> _pendingMenuCancel = [];

    private void SetPendingMenuContext(IScriptObj subject, IReadOnlyList<ScriptKey> keys)
    {
        _pendingMenuSubject = subject;
        _pendingMenuCancel = [];
        bool capture = false;
        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                if (capture) break;
                capture = key.Arg.Trim().Equals("@CANCEL", StringComparison.OrdinalIgnoreCase);
            }
            else if (capture) _pendingMenuCancel.Add(key);
        }
    }

    private void ExecuteMenuResponse(IScriptObj subject, IReadOnlyList<ScriptKey> body)
    {
        if (body.Count == 0) return;
        var interpreter = _triggerDispatcher?.Runner?.Interpreter ??
            new ScriptInterpreter(new ExpressionParser(), NullLogger<ScriptInterpreter>.Instance);
        interpreter.Execute(body, new MenuResponseTarget(this, subject), this,
            new SphereNet.Scripting.Execution.TriggerArgs(_character), new ScriptScope());
    }

    // Route client verbs such as POLY/MAKEITEM through their normal host bridge,
    // while control flow, locals, RETURN and calls use the shared interpreter.
    private sealed class MenuResponseTarget(GameClient client, IScriptObj subject) : IScriptObj
    {
        public string GetName() => subject.GetName();
        public bool TryGetProperty(string key, out string value) => subject.TryGetProperty(key, out value);
        public bool TrySetProperty(string key, string value) => subject.TrySetProperty(key, value);
        public bool TryExecuteCommand(string key, string args, ITextConsole source) =>
            client.TryExecuteScriptCommand(subject, key, args, null) || subject.TryExecuteCommand(key, args, source);
        public TriggerResult OnTrigger(int type, IScriptObj? source, ITriggerArgs? args) => subject.OnTrigger(type, source, args);
    }
}
