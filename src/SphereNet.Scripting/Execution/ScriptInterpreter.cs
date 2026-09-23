using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Script command interpreter. Executes script lines with control flow.
/// Maps to CScriptObj::OnTriggerRun flow in Source-X.
/// Handles IF/ELIF/ELSE/ENDIF, FOR/ENDFOR, WHILE/ENDWHILE, RETURN, DORAND, DOSWITCH, etc.
/// </summary>
public sealed class ScriptInterpreter
{
    private readonly ExpressionParser _expr;
    private readonly ILogger _logger;

    /// <summary>Expression parser used to evaluate <c>&lt;X&gt;</c> and
    /// arithmetic expressions during interpretation. Exposed so higher-level
    /// runtime can plug in per-call resolvers (e.g. dialog button args).</summary>
    public ExpressionParser Expressions => _expr;

    /// <summary>Optional TriggerRunner for CALL verb support.</summary>
    public Func<string, IScriptObj, ITextConsole?, ITriggerArgs?, TriggerResult>? CallFunction { get; set; }
    public Func<string, IScriptObj, ITextConsole?, ITriggerArgs?, ScriptScope, TriggerResult>? CallFunctionWithScope { get; set; }

    /// <summary>
    /// Optional bridge for angle-bracket script function calls that need a
    /// string/numeric return value, e.g. <c>&lt;MyFunc arg1,arg2&gt;</c>.
    /// </summary>
    public Func<string, string, IScriptObj, ITextConsole?, ITriggerArgs?, string?>? ResolveFunctionExpression { get; set; }
    public Func<string, string, IScriptObj, ITextConsole?, ITriggerArgs?, ScriptScope, string?>? ResolveFunctionExpressionWithScope { get; set; }

    /// <summary>Resolves SERV.* and other server-level property lookups from scripts.</summary>
    public Func<string, string?>? ServerPropertyResolver { get; set; }

    // The resolver context, held in fields instead of captured in a lambda.
    //
    // Every ResolveArgs / EvaluateWithResolver / EvaluateConditionWithResolver call
    // used to build two closures and two delegates over (target, source, args,
    // scope) just to hand them to the parser and take them off again. That is four
    // allocations per resolved <X> and per IF condition - the single largest
    // per-line cost in a script body, and it grows with nothing but how many lines
    // the shard runs. The delegates below are created once and read these fields
    // instead; the push/pop pairs save and restore them exactly the way the
    // resolver properties were already being saved and restored, so nesting (a
    // function called from inside a resolver) behaves the same.
    private IScriptObj? _ctxTarget;
    private ITextConsole? _ctxSource;
    private ITriggerArgs? _ctxArgs;
    private ScriptScope? _ctxScope;
    private readonly Func<string, string?> _ctxVarResolver;
    private readonly Func<string, string?> _ctxFuncResolver;

    private readonly record struct ResolverFrame(
        Func<string, string?>? Var, Func<string, string?>? Func,
        IScriptObj? Target, ITextConsole? Source, ITriggerArgs? Args, ScriptScope? Scope);

    private ResolverFrame PushResolvers(
        IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope)
    {
        var saved = new ResolverFrame(_expr.VariableResolver, _expr.FunctionResolver,
            _ctxTarget, _ctxSource, _ctxArgs, _ctxScope);
        _ctxTarget = target; _ctxSource = source; _ctxArgs = args; _ctxScope = scope;
        _expr.VariableResolver = _ctxVarResolver;
        _expr.FunctionResolver = _ctxFuncResolver;
        return saved;
    }

    private void PopResolvers(in ResolverFrame saved)
    {
        _expr.VariableResolver = saved.Var;
        _expr.FunctionResolver = saved.Func;
        _ctxTarget = saved.Target; _ctxSource = saved.Source;
        _ctxArgs = saved.Args; _ctxScope = saved.Scope;
    }

    public ScriptInterpreter(ExpressionParser expr, ILogger<ScriptInterpreter> logger)
    {
        _expr = expr;
        _logger = logger;
        _ctxVarResolver = name => _ctxTarget == null
            ? null : ResolveVarForTarget(name, _ctxTarget, _ctxSource, _ctxArgs, _ctxScope);
        _ctxFuncResolver = text => _ctxTarget == null
            ? null : TryResolveFunctionExpression(text, _ctxTarget, _ctxSource, _ctxArgs, _ctxScope);
    }

    /// <summary>
    /// Execute a list of script keys (lines) within a scope on a target object.
    /// Returns the trigger result.
    /// </summary>
    public TriggerResult Execute(
        IReadOnlyList<ScriptKey> lines,
        IScriptObj target,
        ITextConsole? source,
        ITriggerArgs? args,
        ScriptScope scope)
    {
        var previousResponse = _expr.DialogArgResolver;
        _expr.DialogArgResolver = (args as TriggerArgs)?.DialogResponseResolver;
        try { return ExecuteCore(lines, target, source, args, scope); }
        finally { _expr.DialogArgResolver = previousResponse; }
    }

    private TriggerResult ExecuteCore(
        IReadOnlyList<ScriptKey> lines,
        IScriptObj target,
        ITextConsole? source,
        ITriggerArgs? args,
        ScriptScope scope)
    {
        var result = TriggerResult.Default;
        int i = 0;

        while (i < lines.Count)
        {
            if (scope.IsReturning)
                break;

            var key = lines[i];

            // Push a per-line "where am I" label into the expression parser so
            // any unresolved <X> warning reported during this line names a
            // concrete script file/line instead of just the bare variable.
            // Skipped when the key carries no source info (synthetic keys
            // built in code) — t_currentSourceLabel falls back to the parent
            // frame in that case, which is still better than empty.
            // Only when scriptdebug is on. The label has exactly one consumer,
            // ExpressionParser.ReportUnresolved, and that returns immediately unless
            // DebugUnresolved is set — so with it off this was two string
            // allocations per executed line (a path trim and an interpolation) for a
            // value nothing would ever read. On the live pack's arena that alone was
            // ~8,700 lines/second of pure garbage. The flag is read per line, so
            // toggling scriptdebug still takes effect on the next line.
            using var __srcLabel = _expr.DebugUnresolved && !string.IsNullOrEmpty(key.SourceFile)
                ? _expr.PushSourceLabel(FormatSourceLabel(key, scope))
                : default;

            string cmd = key.KeyUpper;

            // LOCAL./ARGN/ARGS/REFn/FLOAT assignments — shared with the block
            // executors (IF/DORAND/DOSWITCH bodies dispatch single lines and
            // must hit the same handlers; LOCAL sets inside IF blocks used to
            // fall through to a no-op and silently vanish).
            if (TryExecuteAssignmentLine(key, cmd, target, source, args, scope))
            {
                i++;
                continue;
            }

            // CALL and the TRY family are single statements, not blocks, and they are
            // dispatched from the same place wherever they appear.
            if (TryExecuteControlStatement(key, cmd, target, source, args, scope, ref result))
            {
                i++;
                continue;
            }

            if (TryExecuteBlockStatement(lines, cmd, ref i, target, source, args, scope, ref result))
            {
                if (scope.IsReturning || scope.IsBreaking || scope.IsContinuing) break;
                continue;
            }

            switch (cmd)
            {
                case "BREAK":
                    if (scope.LoopDepth > 0)
                    {
                        scope.IsBreaking = true;
                        i = lines.Count;
                    }
                    break;

                case "CONTINUE":
                    if (scope.LoopDepth > 0)
                    {
                        scope.IsContinuing = true;
                        i = lines.Count;
                    }
                    break;

                case "RETURN":
                    result = ApplyReturn(key, target, source, args, scope);
                    i = lines.Count;
                    break;


                // These are block terminators — skip if encountered at top level
                case "ENDIF":
                case "ENDFOR":
                case "ENDWHILE":
                case "END":
                case "ENDDO":
                    i++;
                    break;

                default:
                {
                    ExecuteLine(key, target, source, args, scope);
                    i++;
                    break;
                }
            }
        }

        return result;
    }

    private void ExecuteLine(ScriptKey key, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope scope)
    {
        string resolvedArg = ResolveArgs(key.Arg, target, source, args, scope);
        // Source-X reads verb/property values via GetArgStr, which strips a
        // surrounding quote pair — TAG.X="a b" stores a b, SYSMESSAGE="msg"
        // speaks without the quotes. Values not starting with '"' pass through.
        resolvedArg = ScriptKey.StripQuotePair(resolvedArg);
        // Resolve <…> inside the command key itself. Sphere scripts commonly
        // build tag names dynamically: "Src.CTag0.C<dIdx>=value" — without
        // this pass the literal "<dIdx>" ends up in the key and the setter
        // silently fails.
        string cmd = key.Key.Contains('<')
            ? ResolveArgs(key.Key, target, source, args, scope)
            : key.Key;

        if (_expr.DebugUnresolved)
        {
            _logger.LogDebug("[script_exec] ctx='{Ctx}' cmd='{Cmd}' arg='{Arg}' target='{Target}' source='{Source}'",
                FormatSourceLabel(key, scope),
                cmd,
                resolvedArg,
                target.GetName(),
                source?.GetName() ?? "SYSTEM");
        }

        // CScriptTriggerArgs routes ARGO verbs to the actual argument object,
        // including non-world references such as CClient.
        if (cmd.StartsWith("ARGO.", StringComparison.OrdinalIgnoreCase))
        {
            if (args?.Object1 is { } argumentObject)
            {
                string argumentKey = cmd[5..];
                if (!key.HasArg || !argumentObject.TrySetProperty(argumentKey, resolvedArg))
                    ExecuteVerbLine(argumentKey, resolvedArg, argumentObject, source, args, scope);
            }
            return;
        }

        // Handle SRC. prefix — redirect to source object
        if (cmd.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase))
        {
            string subCmd = cmd[4..];
            IScriptObj? srcObj = args?.Source;
            if (srcObj != null)
            {
                if (key.HasArg && srcObj.TrySetProperty(subCmd, EvalNumericArg(resolvedArg)))
                {
                    if (_expr.DebugUnresolved)
                        _logger.LogDebug("[script_exec] handled via src setprop '{Cmd}'", subCmd);
                    return;
                }
                if (srcObj.TryExecuteCommand(subCmd, resolvedArg, source ?? NullConsole.Instance))
                {
                    if (_expr.DebugUnresolved)
                        _logger.LogDebug("[script_exec] handled via src verb '{Cmd}'", subCmd);
                    return;
                }
                if ((source ?? NullConsole.Instance).TryExecuteScriptCommand(srcObj, subCmd, resolvedArg, args))
                {
                    if (_expr.DebugUnresolved)
                        _logger.LogDebug("[script_exec] handled via src scriptcmd '{Cmd}'", subCmd);
                    return;
                }
                // Source-X: unrecognized SRC.verb → treat as function call on source object
                if (CallFunctionWithScope != null || CallFunction != null)
                {
                    // Pass resolvedArg as the new <ARGS> for the called function
                    // ARGN1/2/3 come from the argument THIS line passes, prepared the
                    // normal way (CScriptTriggerArgs::Init, :112). Copying the
                    // caller's numbers while replacing its string handed the callee a
                    // number from one call and a string from another.
                    var funcArgs = new TriggerArgs
                    {
                        Source = args?.Source,
                        Object1 = args?.Object1,
                        Object2 = args?.Object2,
                    };
                    funcArgs.InitFromRaw(resolvedArg);
                    InvokeFunction(subCmd, srcObj, source, funcArgs, scope);
                    return;
                }
            }
            _logger.LogWarning("SRC not available for: {Key}={Arg}", cmd, resolvedArg);
            return;
        }

        // UID.<hex>.<verb> [args] — direct command on object resolved by
        // UID. Dialog admin scripts lean on this pattern, e.g.
        //     UID.<CTag.Dialog.Admin.C<Eval <ArgN>-10>>.Dialog d_X
        // After ResolveArgs the key looks like "UID.0186A4.DIALOG"; we
        // strip the prefix, look the object up via the same REF bridge
        // REFn uses, and let the host dispatch the verb / setter. When
        // SRC is known, include it so client-bound verbs open on SRC's
        // client while the UID object remains the dialog subject.
        if (cmd.StartsWith("UID.", StringComparison.OrdinalIgnoreCase) && cmd.Length > 4)
        {
            int firstDot = cmd.IndexOf('.', 4);
            if (firstDot > 4)
            {
                string uidTok = cmd[4..firstDot];
                string subCmd = cmd[(firstDot + 1)..];
                if (!string.IsNullOrEmpty(uidTok) && !string.IsNullOrEmpty(subCmd))
                {
                    if (args?.Source != null && args.Source.TryGetProperty("UID", out string srcUid) &&
                        !string.IsNullOrWhiteSpace(srcUid))
                    {
                        ServerPropertyResolver?.Invoke($"_REF_EXEC_AS={srcUid}|{uidTok}|{subCmd}|{resolvedArg}");
                    }
                    else
                    {
                        ServerPropertyResolver?.Invoke($"_REF_EXEC={uidTok}|{subCmd}|{resolvedArg}");
                    }
                    return;
                }
            }
        }

        // VAR.name=value / VAR0.name=value — global variable assignment
        if (cmd.StartsWith("VAR0.", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("VAR.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = cmd.IndexOf('.');
            string varName = cmd[(dot + 1)..];
            ServerPropertyResolver?.Invoke($"_SET_VAR.{varName}={resolvedArg}");
            return;
        }

        // LIST.<name>[.<op>]=value — global list mutation (Source-X
        // CListDefMap::r_LoadVal). Grammar: clear / add / set / append / sort /
        // <index>.remove / <index>.insert / <index>=value. The server side owns
        // the list store, so route the whole expression across the bridge.
        if (cmd.StartsWith("LIST.", StringComparison.OrdinalIgnoreCase))
        {
            string listExpr = cmd[5..];
            ServerPropertyResolver?.Invoke($"_SET_LIST.{listExpr}={resolvedArg}");
            return;
        }

        // OBJ=uid — set global object reference
        if (cmd.Equals("OBJ", StringComparison.OrdinalIgnoreCase) && key.HasArg)
        {
            ServerPropertyResolver?.Invoke($"_SET_OBJ={resolvedArg}");
            return;
        }

        // OBJ.property=value — set property on OBJ reference
        if (cmd.StartsWith("OBJ.", StringComparison.OrdinalIgnoreCase) && key.HasArg)
        {
            ServerPropertyResolver?.Invoke($"_SET_{cmd}={resolvedArg}");
            return;
        }

        // NEW=uid — repoint the global new-object reference (Source-X SSV_NEW,
        // CScriptObj.cpp:1305). NEW had a read path only, so a script restoring the
        // reference it had saved, or dressing the object it had just made through
        // NEW.<prop>, was silently ignored.
        if (cmd.Equals("NEW", StringComparison.OrdinalIgnoreCase) && key.HasArg)
        {
            ServerPropertyResolver?.Invoke($"_SET_NEW={resolvedArg}");
            return;
        }

        // Resolve NEW as an object before dispatch so verbs retain the caller's
        // console/SRC (notably CItem::EQUIP, which uses pSrc->GetChar()).
        if (cmd.StartsWith("NEW.", StringComparison.OrdinalIgnoreCase) &&
            ResolveObjectRef?.Invoke(target, "NEW") is { } newObject)
        {
            string newKey = cmd[4..];
            if (!key.HasArg || !newObject.TrySetProperty(newKey, resolvedArg))
                ExecuteVerbLine(newKey, resolvedArg, newObject, source, args, scope);
            return;
        }

        // NEW.property=value — compatibility for hosts without an object resolver.
        if (cmd.StartsWith("NEW.", StringComparison.OrdinalIgnoreCase) && key.HasArg)
        {
            ServerPropertyResolver?.Invoke($"_SET_{cmd}={resolvedArg}");
            return;
        }

        // CLEARVARS — clear global variables
        if (cmd.Equals("CLEARVARS", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SERV.CLEARVARS", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_CLEARVARS={resolvedArg}");
            return;
        }

        // SHOW — display property value for debugging
        if (cmd.Equals("SHOW", StringComparison.OrdinalIgnoreCase))
        {
            string propName = resolvedArg.Trim();
            if (target.TryGetProperty(propName, out string showVal))
                _logger.LogInformation("SHOW: {Name} = {Value}", propName, showVal);
            else
                _logger.LogInformation("SHOW: {Name} = (undefined)", propName);
            return;
        }

        // NEWDUPE uid — clone an object
        if (cmd.Equals("NEWDUPE", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SERV.NEWDUPE", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_NEWDUPE={resolvedArg}");
            return;
        }

        // Source-X global factories are server verbs, not client verbs. Keeping
        // them here makes SERV.NEWITEM / SERV.NEWNPC work from startup hooks,
        // timers, item triggers and functions that have no connected client.
        if (cmd.Equals("SERV.NEWITEM", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_NEWITEM={resolvedArg}");
            return;
        }
        // The BARE form additionally sets the caller's ACT to what it created
        // (Source-X CScriptObj.cpp:1383, only when the command is not addressed to the
        // server). Both forms used the same server-only bridge, so that half was lost.
        if (cmd.Equals("NEWITEM", StringComparison.OrdinalIgnoreCase))
        {
            string actUid = target.TryGetProperty("UID", out string tuid) ? tuid : "0";
            ServerPropertyResolver?.Invoke($"_NEWITEM_ACT={actUid}|{resolvedArg}");
            return;
        }
        if (cmd.Equals("SERV.NEWNPC", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("NEWNPC", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_NEWNPC={resolvedArg}");
            return;
        }

        // SERV.ALLCLIENTS <function> — invoke <function> once per online
        // client, with the current target staying as src. Used by admin
        // scripts to tally players, push messages, etc. The server-side
        // iterator lives in Program.cs behind the _ALLCLIENTS= protocol.
        if (cmd.Equals("SERV.ALLCLIENTS", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("ALLCLIENTS", StringComparison.OrdinalIgnoreCase))
        {
            string srcUid = args?.Source != null && args.Source.TryGetProperty("UID", out string suid)
                ? suid : "0";
            ServerPropertyResolver?.Invoke($"_ALLCLIENTS={srcUid}|{resolvedArg}");
            return;
        }

        // SERV.VARLIST [prefix] / SERV.PRINTLISTS — diagnostic dumps written to the
        // caller's console (Source-X output goes to the invoking client), not just the
        // server log. srcUid is carried so the server side can find that console.
        if (cmd.Equals("SERV.VARLIST", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("VARLIST", StringComparison.OrdinalIgnoreCase))
        {
            string srcUid = args?.Source != null && args.Source.TryGetProperty("UID", out string suid)
                ? suid : "0";
            ServerPropertyResolver?.Invoke($"_VARLIST={srcUid}|{resolvedArg}");
            return;
        }
        if (cmd.Equals("SERV.PRINTLISTS", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("PRINTLISTS", StringComparison.OrdinalIgnoreCase))
        {
            string srcUid = args?.Source != null && args.Source.TryGetProperty("UID", out string suid)
                ? suid : "0";
            ServerPropertyResolver?.Invoke($"_PRINTLISTS={srcUid}");
            return;
        }

        // SERV.BLOCKIP <ip>[,seconds] / SERV.UNBLOCKIP <ip> — Source-X gates
        // these at PLEVEL_Admin; srcUid is carried so the host can verify the
        // caller's privilege (0 = server/hook context).
        if (cmd.Equals("SERV.BLOCKIP", StringComparison.OrdinalIgnoreCase))
        {
            string srcUid = args?.Source != null && args.Source.TryGetProperty("UID", out string suid)
                ? suid : "0";
            ServerPropertyResolver?.Invoke($"_BLOCKIP={srcUid}|{resolvedArg}");
            return;
        }
        if (cmd.Equals("SERV.UNBLOCKIP", StringComparison.OrdinalIgnoreCase))
        {
            string srcUid = args?.Source != null && args.Source.TryGetProperty("UID", out string suid)
                ? suid : "0";
            ServerPropertyResolver?.Invoke($"_UNBLOCKIP={srcUid}|{resolvedArg}");
            return;
        }

        // SERV.CALCCRYPT <ver>[,cliType][,encType] — prints the computed
        // SphereCrypt.ini-style key line to the caller's console.
        if (cmd.Equals("SERV.CALCCRYPT", StringComparison.OrdinalIgnoreCase))
        {
            string srcUid = args?.Source != null && args.Source.TryGetProperty("UID", out string suid)
                ? suid : "0";
            ServerPropertyResolver?.Invoke($"_CALCCRYPT={srcUid}|{resolvedArg}");
            return;
        }

        // SERV.B <text> — broadcast to every connected client (Source-X SV_B /
        // CWorldComm::Broadcast). Was console-only; a script's serv.b was a no-op.
        if (cmd.Equals("SERV.B", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SERV.BROADCAST", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_BROADCAST={resolvedArg}");
            return;
        }

        // SERV.GARBAGE — force the maintenance/GC pass (console GARBAGE).
        if (cmd.Equals("SERV.GARBAGE", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke("_GARBAGE=");
            return;
        }

        // SERV.SAVE / SERV.RESPAWN / SERV.RESTOCK — server verbs (CServer::r_Verb
        // SV_SAVE / SV_RESPAWN / SV_RESTOCK). Written as a script line they reached
        // no handler and did nothing, with no warning; the host answers the same
        // names as the console commands.
        if (cmd.Equals("SERV.SAVE", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SERV.RESPAWN", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SERV.RESTOCK", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke(cmd[5..].ToUpperInvariant());
            return;
        }

        // SERV.SHRINKMEM — Source-X SetProcessWorkingSetSize; here a managed
        // compacting GC pass.
        if (cmd.Equals("SERV.SHRINKMEM", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke("_SHRINKMEM=");
            return;
        }

        // SERV.SECURE — toggle secure mode (blocks the script/console
        // SHUTDOWN while enabled, Source-X SetSecure).
        if (cmd.Equals("SERV.SECURE", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke("_SECUREMODE=");
            return;
        }

        // SERV.HEARALL toggles player-speech logging through the server resolver.
        if (cmd.Equals("SERV.HEARALL", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_HEARALL={resolvedArg}");
            return;
        }

        // SERV.INFORMATION — server status lines to the caller's console
        // (same caller-routing protocol as SERV.VARLIST).
        if (cmd.Equals("SERV.INFORMATION", StringComparison.OrdinalIgnoreCase))
        {
            string srcUid = args?.Source != null && args.Source.TryGetProperty("UID", out string suid)
                ? suid : "0";
            ServerPropertyResolver?.Invoke($"_INFORMATION={srcUid}");
            return;
        }

        if (cmd.Equals("SERV.EXPORT", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("EXPORT", StringComparison.OrdinalIgnoreCase))
        {
            string targetUid = target.TryGetProperty("UID", out string tuid) ? tuid : "0";
            ServerPropertyResolver?.Invoke($"_EXPORT={targetUid}|{resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.LOAD", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("LOAD", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_LOAD={resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.IMPORT", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("IMPORT", StringComparison.OrdinalIgnoreCase))
        {
            string targetUid = target.TryGetProperty("UID", out string tuid) ? tuid : "0";
            ServerPropertyResolver?.Invoke($"_IMPORT={targetUid}|{resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.RESTORE", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("RESTORE", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_RESTORE={resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.SAVESTATICS", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SAVESTATICS", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_SAVESTATICS={resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.WRITEFILE", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("WRITEFILE", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_WRITEFILE={resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.DELETEFILE", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("DELETEFILE", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_DELETEFILE={resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.CONSOLE", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_CONSOLE={resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.LOG", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("LOG", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_LOG={resolvedArg}");
            return;
        }

        if (cmd.Equals("SERV.GMPAGE", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SENDGMPAGE", StringComparison.OrdinalIgnoreCase))
        {
            string srcUid = args?.Source != null && args.Source.TryGetProperty("UID", out string suid)
                ? suid : "";
            ServerPropertyResolver?.Invoke($"_GMPAGE={srcUid}|{resolvedArg}");
            return;
        }

        // DB/LDB/MDB are global Source-X references. Route their verbs through
        // the host so they also work in server hooks where no GameClient console
        // exists (f_onserver_start is the canonical MDB.IMPORTDB caller).
        if (cmd.StartsWith("DB.", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("LDB.", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("MDB.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = cmd.IndexOf('.');
            ServerPropertyResolver?.Invoke($"_DB_VERB={cmd[..dot]}|{cmd[(dot + 1)..]}|{resolvedArg}");
            return;
        }

        // DEFMSG name=value — set default message
        if (cmd.Equals("DEFMSG", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_SET_DEFMSG={resolvedArg}");
            return;
        }

        // SERV.SEASON <value> — global season setter routed through the
        // server resolver so scripts can drive the authoritative weather state.
        if (cmd.Equals("SERV.SEASON", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_SET_SEASON={resolvedArg}");
            return;
        }

        // ARGS= updates the current trigger args string so subsequent
        // <ARGV[N]> accessors see the new token list. Sphere moongate
        // dialog handlers rely on this:
        //   args=<def0.moongates_facet0_0>   // "4467,1283,5,0,Moonglow"
        //   src.p=<argv[0]>,<argv[1]>,...    // parses the comma-split list
        if (cmd.Equals("ARGS", StringComparison.OrdinalIgnoreCase) && args is TriggerArgs mutableArgs)
        {
            mutableArgs.ArgString = resolvedArg ?? "";
            if (_expr.DebugUnresolved)
                _logger.LogDebug("[script_exec] updated ARGS='{Args}'", mutableArgs.ArgString);
            return;
        }

        // Try as property set (KEY=VALUE)
        if (TryPreferredFunction(cmd, resolvedArg, target, source, args, scope))
            return;

        if (key.HasArg && target.TrySetProperty(cmd, EvalNumericArg(cmd, resolvedArg)))
        {
            if (_expr.DebugUnresolved)
                _logger.LogDebug("[script_exec] handled via setprop '{Cmd}'", cmd);
            return;
        }

        // A dialog belongs to the source client, never implicitly to the
        // target's client. In particular TRYSRV has no client to receive it.
        if (cmd.Equals("DIALOG", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SDIALOG", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("DIALOGCLOSE", StringComparison.OrdinalIgnoreCase))
        {
            ExecuteVerbLine(cmd, resolvedArg, target, source, args, scope);
            return;
        }

        // Try as verb/command
        if (target.TryExecuteCommand(cmd, resolvedArg, source ?? NullConsole.Instance))
        {
            if (_expr.DebugUnresolved)
                _logger.LogDebug("[script_exec] handled via target verb '{Cmd}'", cmd);
            return;
        }

        // Source-X bridge: allow console/client host to handle script-specific verbs
        // (targetf/targetfg, dialog, serv.*, db.*, etc.) without polluting object classes.
        if ((source ?? NullConsole.Instance).TryExecuteScriptCommand(target, cmd, resolvedArg, args))
        {
            if (_expr.DebugUnresolved)
                _logger.LogDebug("[script_exec] handled via source scriptcmd '{Cmd}'", cmd);
            return;
        }

        // Source-X: any unrecognized command is treated as a function call
        if (CallFunctionWithScope != null || CallFunction != null)
        {
            // The argument this line passes becomes the callee's ARGS *and* its
            // ARGN1/2/3, prepared the normal way (CScriptTriggerArgs::Init, :112);
            // the caller's own numbers do not carry over.
            var funcArgs = new TriggerArgs
            {
                Source = args?.Source,
                Object1 = args?.Object1,
                Object2 = args?.Object2,
            };
            funcArgs.InitFromRaw(resolvedArg);
            InvokeFunction(cmd, target, source, funcArgs, scope);
            if (_expr.DebugUnresolved)
                _logger.LogDebug("[script_exec] delegated to function '{Cmd}'", cmd);
            return;
        }

        // FILE.* verbs from a context without a client console (server hooks,
        // NPC triggers) — Source-X serves these from the server-global
        // g_Serv._hFile; route to the host's FILE dispatcher. Client consoles
        // already handled the verb above, so this is the no-console fallback.
        if (cmd.StartsWith("FILE.", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke(
                resolvedArg.Length > 0 ? $"{cmd} {resolvedArg}" : cmd);
            return;
        }

        _logger.LogWarning("Unhandled script line: {Key}={Arg}", cmd, resolvedArg);
    }

    /// <summary>Scope/trigger-arg assignment lines (LOCAL.x=, ARGN/ARGS, REFn=,
    /// REFn.prop=, FLOAT.x=). Returns true when the line was consumed. Called
    /// from the main loop AND from every block executor that dispatches single
    /// lines (IF/DORAND/DOSWITCH) — routing those through ExecuteLine alone
    /// silently dropped LOCAL writes inside IF blocks.</summary>
    private bool TryExecuteAssignmentLine(ScriptKey key, string cmd, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope)
    {
        // LOCAL.varname=value (Key="LOCAL.foo", Arg="bar")
        if (cmd.StartsWith("LOCAL.", StringComparison.Ordinal))
        {
            string localExpr = cmd[6..] + (key.HasArg ? "=" + ResolveArgs(key.Arg, target, source, args, scope) : "");
            int eqIdx = localExpr.IndexOf('=');
            if (eqIdx > 0)
            {
                string varName = localExpr[..eqIdx].Trim();
                // Source-X CScriptTriggerArgs::r_LoadVal reads the value with
                // GetArgStr (CScriptTriggerArgs.cpp:246): a leading quote and the
                // LAST quote are the script's, not the value's. Keeping them put
                // LOCAL.Data = "{...}" into an SQL insert as "\"{...}\"" - a JSON
                // string, not an object - and every json_extract over the table failed.
                string varVal = ScriptKey.StripQuotePair(localExpr[(eqIdx + 1)..].Trim());
                scope.LocalVars.Set(varName, varVal);
            }
            return true;
        }

        // ARGN1 / ARGN2 / ARGN3 (bare ARGN == ARGN1) assignment — a script
        // mutates the trigger's numeric args (Source-X @Hit damage, @DropOn drop-Z,
        // @NPCActFight dist/motivation). TriggerDispatcher.RunWrapped copies these
        // back into the caller's TriggerArgs after the block.
        if (args is TriggerArgs argnTarget &&
            cmd is "ARGN" or "ARGN1" or "ARGN2" or "ARGN3")
        {
            string rawVal = ResolveArgs(key.Arg, target, source, args, scope).Trim();
            // The value is a Sphere expression, not a decimal-or-0x int: upstream
            // assigns through GetArgVal (CScriptTriggerArgs.cpp:313), which is the
            // full expression parser. The narrow parser read "010" as ten instead of
            // sixteen and rejected "1+1" outright, leaving the previous ARGN standing.
            long argnVal = 0;
            if (rawVal.Length == 0 || TryEvaluateWithResolver(rawVal, target, source, args, scope, out argnVal))
            {
                if (cmd == "ARGN2") argnTarget.Number2 = argnVal;
                else if (cmd == "ARGN3") argnTarget.Number3 = argnVal;
                else argnTarget.Number1 = argnVal;
            }
            return true;
        }

        // Source-X AGC_S invokes Init(GetArgStr()), including for an empty line:
        // rebuild ARGN/ARGV and clear ARGO, retaining LOCAL/FLOAT/REF pools.
        if (args is TriggerArgs argsTarget && cmd == "ARGS")
        {
            argsTarget.InitFromRaw(ScriptKey.StripQuotePair(ResolveArgs(key.Arg, target, source, args, scope)));
            return true;
        }

        // REFn=value (Key="REF1", Arg="<UID>")
        if (cmd.StartsWith("REF", StringComparison.Ordinal) && cmd.Length > 3 &&
            char.IsDigit(cmd[3]) && !cmd.Contains('.'))
        {
            if (int.TryParse(cmd.AsSpan(3), out int refIdx))
            {
                string refVal = ResolveArgs(key.Arg, target, source, args, scope);
                // CScriptTriggerArgs uses GetArgVal: decimal UIDs and arithmetic
                // must name the same object as an explicitly hexadecimal UID.
                uint refSerial = TryEvaluateWithResolver(refVal, target, source, args, scope, out long numericRef)
                    ? unchecked((uint)numericRef) : 0;
                string canonicalRef = refSerial == 0 ? "0" : $"0{refSerial:X}";
                if (refSerial != 0 && ServerPropertyResolver?.Invoke($"_REF_GET={canonicalRef}|UID") == "0")
                    canonicalRef = "0";
                scope.SetRef(refIdx, canonicalRef);
            }
            return true;
        }

        // REFn.property=value — set property on / execute against the referenced object
        if (cmd.StartsWith("REF", StringComparison.Ordinal) && cmd.Length > 3 && char.IsDigit(cmd[3]))
        {
            int dotIdx = cmd.IndexOf('.');
            if (dotIdx > 3 && int.TryParse(cmd.AsSpan(3, dotIdx - 3), out int refIdx2))
            {
                string refUid = scope.GetRef(refIdx2);
                string subCmd = cmd[(dotIdx + 1)..];
                string resolvedVal = ResolveArgs(key.Arg, target, source, args, scope);
                if (ResolveObjectRef?.Invoke(target, $"UID.{refUid}") is { } referencedObject)
                {
                    if (!key.HasArg || !referencedObject.TrySetProperty(subCmd, resolvedVal))
                        ExecuteVerbLine(subCmd, resolvedVal, referencedObject, source, args, scope);
                }
                else
                    ServerPropertyResolver?.Invoke($"_REF_EXEC={refUid}|{subCmd}|{resolvedVal}");
            }
            return true;
        }

        // FLOAT.name=value
        if (cmd.StartsWith("FLOAT.", StringComparison.OrdinalIgnoreCase))
        {
            string floatName = cmd[6..];
            if (key.HasArg)
            {
                // GetArgStr, as for LOCAL (CScriptTriggerArgs.cpp:240).
                string floatVal = ScriptKey.StripQuotePair(ResolveArgs(key.Arg, target, source, args, scope).Trim());
                scope.SetFloat(floatName, floatVal);
            }
            return true;
        }

        return false;
    }

    /// <summary>What a RETURN line does, wherever it is written: store the value on
    /// the scope, mark the block as returning, and report True for a non-zero number.
    /// The string result preserves the expanded argument, independently of the
    /// numeric trigger control result (Source-X SK_RETURN with pResult).</summary>
    /// <summary>Run a CALL or a TRY-family statement. Both are single lines rather
    /// than blocks, and both used to live only in Execute's switch - so inside an IF,
    /// or as the line a DORAND picked, they fell to ExecuteLine, which does not know
    /// them, and did nothing at all. The shipped packs write 84 of them inside an IF.
    ///
    /// Returns false for anything else, leaving it to the caller.</summary>
    private bool TryExecuteControlStatement(ScriptKey line, string cmd, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, ref TriggerResult result)
    {
        switch (cmd)
        {
                case "CALL":
                {
                    // Source-X Execute_Call (CScriptObj.cpp:1505):
                    //  * a reference head (SRC., TOPOBJ., CONT., LINK., ...) redirects
                    //    the call to that object before the name is looked up;
                    //  * WITH an argument the CALLER'S OWN args object is temporarily
                    //    re-Init'ed from it and restored afterwards, so the callee sees
                    //    ARGN/ARGS built from the new argument and the caller gets its
                    //    own back;
                    //  * WITHOUT one the caller's args go through untouched, so the
                    //    callee sees the caller's ARGN1 and ARGS;
                    //  * either way it is the SAME args object, so the LOCAL pool is
                    //    shared both ways.
                    // Building a fresh args object copied the caller's NUMBERS while
                    // replacing its STRING: "CALL f_child 37" gave the child ARGN1=17
                    // with ARGS=37, and "CALL f_child" wiped ARGS instead of passing it.
                    string call = ResolveArgs(line.Arg, target, source, args, scope).Trim();
                    int split = call.IndexOfAny([' ', '	']);
                    string callHead = split < 0 ? call : call[..split].Trim();
                    string funcArgString = split < 0 ? "" : call[(split + 1)..].Trim();

                    var callTarget = ResolveCallReference(ref callHead, target, source, args);
                    if (callTarget != null && !string.IsNullOrEmpty(callHead))
                    {
                        if (args is not TriggerArgs callerArgs)
                        {
                            var fresh = new TriggerArgs { Source = args?.Source };
                            fresh.InitFromRaw(funcArgString);
                            fresh.ShareCallerLocals = true;
                            if (InvokeFunction(callHead, callTarget, source, fresh, scope) == TriggerResult.True)
                                result = TriggerResult.True;
                        }
                        else
                        {
                            bool hasArg = funcArgString.Length > 0;
                            long n1 = callerArgs.Number1, n2 = callerArgs.Number2, n3 = callerArgs.Number3;
                            var o1 = callerArgs.Object1;
                            string savedArgs = callerArgs.ArgString;
                            bool savedShare = callerArgs.ShareCallerLocals;

                            if (hasArg)
                                callerArgs.InitFromRaw(funcArgString);
                            callerArgs.ShareCallerLocals = true;
                            try
                            {
                                if (InvokeFunction(callHead, callTarget, source, callerArgs, scope) == TriggerResult.True)
                                    result = TriggerResult.True;
                            }
                            finally
                            {
                                callerArgs.ShareCallerLocals = savedShare;
                                if (hasArg)
                                {
                                    // The LOCAL pool is deliberately NOT restored: it is
                                    // shared, and what the callee wrote is meant to be
                                    // visible to the caller.
                                    callerArgs.Number1 = n1;
                                    callerArgs.Number2 = n2;
                                    callerArgs.Number3 = n3;
                                    callerArgs.Object1 = o1;
                                    callerArgs.ArgString = savedArgs;
                                }
                            }
                        }
                    }
                    return true;
                }

                // TRY — execute the rest of the line as a normal command (obsolete in Source-X but kept for compat)
                case "TRY":
                {
                    string tryLine = ResolveArgs(line.Arg, target, source, args, scope);
                    if (!string.IsNullOrWhiteSpace(tryLine))
                    {
                        // Parse only the first separator. An '=' inside an
                        // argument is data, not a property assignment; '=' and
                        // ',' after the verb must still reach script functions.
                        ScriptCommandLine.Split(tryLine, out string verb, out string verbArgs);
                        ExecuteVerbLine(verb, verbArgs, target, source, args, scope);
                    }
                    return true;
                }

                // TRYSRV — execute with SERV as source context (PLEVEL 7)
                case "TRYSRV":
                {
                    string srvLine = ResolveArgs(line.Arg, target, source, args, scope);
                    if (!string.IsNullOrWhiteSpace(srvLine))
                    {
                        ScriptCommandLine.Split(srvLine, out string verb, out string verbArgs);
                        // Do not retain the caller's character in TriggerArgs or
                        // fall back to their client when the server verb refuses.
                        ExecuteVerbLine(verb, verbArgs, target, ScriptServerConsole.Instance, null, scope);
                    }
                    return true;
                }

                // One native path also serves TIMERF and reference-prefixed calls:
                // TRYSRC changes the source console while retaining this target.
                case "TRYSRC":
                {
                    string srcLine = ResolveArgs(line.Arg, target, source, args, scope);
                    target.TryExecuteCommand("TRYSRC", srcLine, source ?? NullConsole.Instance);
                    return true;
                }

                // Native TRYP owns privilege and world-object touch checks for
                // both interpreted and delayed calls.
                case "TRYP":
                {
                    string payload = ResolveArgs(line.Arg, target, source, args, scope);
                    target.TryExecuteCommand("TRYP", payload, source ?? NullConsole.Instance);
                    return true;
                }

                // TRYLEVEL <plevel> <verb args...> — same as TRY but fail closed
                // when there is no concrete source to authorize.
                case "TRYLEVEL":
                {
                    string tryLevelLine = ResolveArgs(line.Arg, target, source, args, scope);
                    int firstSpace = tryLevelLine.IndexOf(' ');
                    string plevelTok = firstSpace > 0 ? tryLevelLine[..firstSpace].Trim() : tryLevelLine.Trim();
                    string rest = firstSpace > 0 ? tryLevelLine[(firstSpace + 1)..].Trim() : "";
                    if (!int.TryParse(plevelTok, out int minPlevel) ||
                        string.IsNullOrWhiteSpace(rest) ||
                        source == null ||
                        (int)(source?.GetPrivLevel() ?? PrivLevel.Guest) < minPlevel)
                    {
                        return true;
                    }

                    int spIdx = rest.IndexOf(' ');
                    string verb = spIdx > 0 ? rest[..spIdx].Trim() : rest.Trim();
                    string verbArgs = spIdx > 0 ? rest[(spIdx + 1)..].Trim() : "";
                    var actor = source!;
                    if (!target.TryExecuteCommand(verb, verbArgs, actor))
                        actor.TryExecuteScriptCommand(target, verb, verbArgs, args);
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>Run a block-opening statement, whatever block it is nested in.
    ///
    /// Execute and ExecuteIf both need this, and ExecuteIf used to carry a partial
    /// copy of it: IF, FOR and WHILE had cases there, the other block kinds did not,
    /// and so they fell through to ExecuteLine as ordinary statements. A DORAND inside
    /// an IF ran EVERY option instead of one - the shipped packs write 4754 of those,
    /// most of them the guarded random barkline of a town NPC - and an object loop ran
    /// its body once, on the wrong object, instead of iterating.
    ///
    /// Returns false when the statement opens no block, leaving it to the caller.
    /// </summary>
    private bool TryExecuteBlockStatement(IReadOnlyList<ScriptKey> lines, string cmd, ref int i,
        IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope scope,
        ref TriggerResult result)
    {
        switch (cmd)
        {
            case "IF":
                i = ExecuteIf(lines, i, target, source, args, scope, out result);
                return true;
            case "FOR":
                i = ExecuteFor(lines, i, target, source, args, scope, out result);
                return true;
            case "WHILE":
                i = ExecuteWhile(lines, i, target, source, args, scope, out result);
                return true;
            case "DORAND":
                i = ExecuteDoRand(lines, i, target, source, args, scope, out result);
                return true;
            case "DOSWITCH":
                i = ExecuteDoSwitch(lines, i, target, source, args, scope, out result);
                return true;
            case "BEGIN":
                i = ExecuteBegin(lines, i, target, source, args, scope, out result);
                return true;
            default:
                if (!IsForVariant(cmd))
                    return false;
                i = ExecuteForObjects(lines, i, target, source, args, scope, out result, cmd);
                return true;
        }
    }

    private TriggerResult ApplyReturn(ScriptKey line, IScriptObj target, ITextConsole? source,
        ITriggerArgs? args, ScriptScope scope)
    {
        string argStr = ResolveArgs(line.Arg, target, source, args, scope);
        // Source-X copies the expanded argument verbatim when a function result
        // is requested. A registered defname is still text here, not its ID.
        // Keep numeric trigger control flow separate from the returned string.
        TryEvaluateWithResolver(argStr, target, source, args, scope, out long val);
        scope.ReturnValue = argStr;
        scope.NumericReturnValue = val;
        scope.IsReturning = true;
        return val != 0 ? TriggerResult.True : TriggerResult.Default;
    }

    private int ExecuteIf(IReadOnlyList<ScriptKey> lines, int startIdx, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, out TriggerResult result)
    {
        result = TriggerResult.Default;
        int i = startIdx;

        string condition = ResolveArgs(lines[i].Arg, target, source, args, scope);
        bool condResult = EvaluateConditionWithResolver(condition, target, source, args, scope);
        bool branchTaken = condResult;
        i++;

        while (i < lines.Count)
        {
            string cmd = lines[i].KeyUpper;

            if (cmd == "ENDIF")
            {
                i++;
                break;
            }

            if (cmd == "ELSE")
            {
                condResult = !branchTaken;
                branchTaken = true;
                i++;
                continue;
            }

            if (cmd == "ELIF" || cmd == "ELSEIF")
            {
                if (!branchTaken)
                {
                    string elifCond = ResolveArgs(lines[i].Arg, target, source, args, scope);
                    condResult = EvaluateConditionWithResolver(elifCond, target, source, args, scope);
                    if (condResult)
                        branchTaken = true;
                }
                else
                {
                    condResult = false;
                }
                i++;
                continue;
            }

            if (condResult)
            {
                // Every block kind, through the one dispatcher Execute uses. This used
                // to be a partial copy with IF, FOR and WHILE in it; the rest fell to
                // ExecuteLine below and their block structure was simply ignored.
                if (TryExecuteBlockStatement(lines, cmd, ref i, target, source, args, scope, ref result))
                {
                    if (scope.IsReturning || scope.IsBreaking || scope.IsContinuing)
                        return lines.Count;
                    continue;
                }

                if (TryExecuteControlStatement(lines[i], cmd, target, source, args, scope, ref result))
                {
                    i++;
                    continue;
                }

                switch (cmd)
                {
                    // The out-parameter used to keep the Default it was initialised
                    // with, so a RETURN 1 inside an IF stopped the block and then
                    // reported nothing - and "IF <condition> ... RETURN 1 ... ENDIF" is
                    // how every conditional veto in every pack is written, from
                    // @EquipTest to @DClick to @Buy. Loops were never affected: they
                    // run their body through Execute, which maps it.
                    case "RETURN":
                        result = ApplyReturn(lines[i], target, source, args, scope);
                        return lines.Count;
                    // BREAK and CONTINUE are loop control, and "IF <cond> BREAK ENDIF"
                    // is how a loop is exited conditionally - there is no other way to
                    // write it. Neither had a case here, so both fell to ExecuteLine,
                    // which does not know them: the loop ran to its end every time, and
                    // a search loop kept going past the match it had already found.
                    case "BREAK":
                        if (scope.LoopDepth > 0)
                        {
                            scope.IsBreaking = true;
                            return lines.Count;
                        }
                        i++;
                        break;

                    case "CONTINUE":
                        if (scope.LoopDepth > 0)
                        {
                            scope.IsContinuing = true;
                            return lines.Count;
                        }
                        i++;
                        break;

                    default:
                        if (!TryExecuteAssignmentLine(lines[i], cmd, target, source, args, scope))
                            ExecuteLine(lines[i], target, source, args, scope);
                        i++;
                        break;
                }

                // A nested block may have set any of the three; leaving the IF is the
                // only way they reach the loop that has to act on them.
                if (scope.IsReturning || scope.IsBreaking || scope.IsContinuing)
                    return lines.Count;
            }
            else
            {
                // Skip nested blocks
                i = SkipBlock(lines, i, cmd);
            }
        }

        return i;
    }

    private int ExecuteFor(IReadOnlyList<ScriptKey> lines, int startIdx, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, out TriggerResult result)
    {
        result = TriggerResult.Default;
        int i = startIdx;

        // Source-X FOR argument forms (CScriptObj.cpp):
        //   FOR x            -> _FOR = 1..x
        //   FOR min max      -> _FOR = min..max
        //   FOR var max      -> var  = 1..max   (first token is a name)
        //   FOR var min max  -> var  = min..max
        // Ranges are INCLUSIVE and count DOWN when min > max.
        var tokens = TokenizeForArgs(lines[i].Arg);
        i++;

        string loopVar = "_FOR";
        long min = 1, max = 1;
        long Eval(string tok) =>
            EvaluateWithResolver(ResolveArgs(tok, target, source, args, scope), target, source, args, scope);

        if (tokens.Count == 1)
        {
            min = 1; max = Eval(tokens[0]);
        }
        else if (tokens.Count == 2)
        {
            if (IsLoopVarName(tokens[0])) { loopVar = tokens[0]; min = 1; max = Eval(tokens[1]); }
            else { min = Eval(tokens[0]); max = Eval(tokens[1]); }
        }
        else if (tokens.Count >= 3)
        {
            loopVar = tokens[0]; min = Eval(tokens[1]); max = Eval(tokens[2]);
        }

        int bodyStart = i;
        int bodyEnd = FindBlockEnd(lines, bodyStart, "ENDFOR");

        scope.LoopDepth++;
        var forBody = GetSubList(lines, bodyStart, bodyEnd);
        bool countDown = min > max;
        long iterations = 0;
        for (long v = min;
             (countDown ? v >= max : v <= max) && iterations < scope.MaxLoopIterations;
             v += countDown ? -1 : 1, iterations++)
        {
            scope.LocalVars.SetInt("_FOR", v);
            if (!loopVar.Equals("_FOR", StringComparison.OrdinalIgnoreCase))
                scope.LocalVars.SetInt(loopVar, v);
            result = Execute(forBody, target, source, args, scope);
            if (scope.IsContinuing) { scope.IsContinuing = false; continue; }
            if (scope.IsBreaking) { scope.IsBreaking = false; break; }
            if (scope.IsReturning) break;
        }
        scope.LoopDepth--;

        return bodyEnd + 1;
    }

    /// <summary>Split a FOR argument list on spaces/commas, respecting &lt;...&gt;
    /// and (...) so a bracketed sub-expression stays one token.</summary>
    private static List<string> TokenizeForArgs(string s)
    {
        var tokens = new List<string>();
        int angle = 0, paren = 0, start = 0;
        bool inTok = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') angle++;
            else if (c == '>' && angle > 0) angle--;
            else if (c == '(') paren++;
            else if (c == ')' && paren > 0) paren--;

            bool sep = (char.IsWhiteSpace(c) || c == ',') && angle == 0 && paren == 0;
            if (sep)
            {
                if (inTok) { tokens.Add(s[start..i]); inTok = false; }
            }
            else if (!inTok) { start = i; inTok = true; }
        }
        if (inTok) tokens.Add(s[start..]);
        return tokens;
    }

    /// <summary>A FOR loop-variable name is a bare identifier (letters/digits/_,
    /// starting with a letter or _) — not a number or a &lt;...&gt; expression.</summary>
    private static bool IsLoopVarName(string tok)
    {
        if (string.IsNullOrEmpty(tok)) return false;
        if (!(char.IsLetter(tok[0]) || tok[0] == '_')) return false;
        foreach (char c in tok)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    private int ExecuteWhile(IReadOnlyList<ScriptKey> lines, int startIdx, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, out TriggerResult result)
    {
        result = TriggerResult.Default;
        int i = startIdx;

        string condition = lines[i].Arg;
        i++;

        int bodyStart = i;
        int bodyEnd = FindBlockEnd(lines, bodyStart, "ENDWHILE");

        scope.LoopDepth++;
        var whileBody = GetSubList(lines, bodyStart, bodyEnd);
        int iterations = 0;
        while (iterations < scope.MaxLoopIterations)
        {
            string resolved = ResolveArgs(condition, target, source, args, scope);
            if (!EvaluateConditionWithResolver(resolved, target, source, args, scope))
                break;

            result = Execute(whileBody, target, source, args, scope);
            if (scope.IsContinuing) { scope.IsContinuing = false; iterations++; continue; }
            if (scope.IsBreaking) { scope.IsBreaking = false; break; }
            if (scope.IsReturning) break;
            iterations++;
        }
        scope.LoopDepth--;

        return bodyEnd + 1;
    }

    private int ExecuteDoRand(IReadOnlyList<ScriptKey> lines, int startIdx, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, out TriggerResult result)
    {
        result = TriggerResult.Default;
        int i = startIdx + 1;

        // Collect lines until ENDDO
        var options = new List<int>();
        while (i < lines.Count && !lines[i].Key.Equals("ENDDO", StringComparison.OrdinalIgnoreCase))
        {
            options.Add(i);
            i++;
        }

        if (options.Count > 0)
        {
            int pick = Random.Shared.Next(options.Count);
            var picked = lines[options[pick]];
            // The picked line can be a RETURN, and a lookup table written as a DOSWITCH
            // of RETURNs is the ordinary way to write one. ExecuteLine does not know
            // RETURN, so the value went nowhere and the function returned blank.
            string pickedCmd = picked.KeyUpper;
            if (pickedCmd == "RETURN")
                result = ApplyReturn(picked, target, source, args, scope);
            else if (!TryExecuteAssignmentLine(picked, pickedCmd, target, source, args, scope) &&
                     !TryExecuteControlStatement(picked, pickedCmd, target, source, args, scope, ref result))
                ExecuteLine(picked, target, source, args, scope);
        }

        return i < lines.Count ? i + 1 : i;
    }

    private int ExecuteDoSwitch(IReadOnlyList<ScriptKey> lines, int startIdx, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, out TriggerResult result)
    {
        result = TriggerResult.Default;
        string indexStr = ResolveArgs(lines[startIdx].Arg, target, source, args, scope);
        int switchIdx = (int)EvaluateWithResolver(indexStr, target, source, args, scope);
        int i = startIdx + 1;

        int lineIdx = 0;
        while (i < lines.Count && !lines[i].Key.Equals("ENDDO", StringComparison.OrdinalIgnoreCase))
        {
            if (lineIdx == switchIdx)
            {
                string pickedCmd = lines[i].KeyUpper;
                if (pickedCmd == "RETURN")
                    result = ApplyReturn(lines[i], target, source, args, scope);
                else if (!TryExecuteAssignmentLine(lines[i], pickedCmd, target, source, args, scope) &&
                         !TryExecuteControlStatement(lines[i], pickedCmd, target, source, args, scope, ref result))
                    ExecuteLine(lines[i], target, source, args, scope);
                break;
            }
            lineIdx++;
            i++;
        }

        while (i < lines.Count && !lines[i].Key.Equals("ENDDO", StringComparison.OrdinalIgnoreCase))
            i++;

        return i < lines.Count ? i + 1 : i;
    }

    private int ExecuteBegin(IReadOnlyList<ScriptKey> lines, int startIdx, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, out TriggerResult result)
    {
        int i = startIdx + 1;
        int bodyEnd = FindBlockEnd(lines, i, "END");
        result = Execute(GetSubList(lines, i, bodyEnd), target, source, args, scope);
        return bodyEnd + 1;
    }

    private int ExecuteForObjects(IReadOnlyList<ScriptKey> lines, int startIdx, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, out TriggerResult result, string queryKind)
    {
        result = TriggerResult.Default;
        var console = source ?? NullConsole.Instance;
        string queryArg = ResolveArgs(lines[startIdx].Arg, target, source, args, scope);

        int i = startIdx + 1;
        int bodyEnd = FindBlockEnd(lines, i, "ENDFOR");

        var body = GetSubList(lines, i, bodyEnd);
        var objects = console.QueryScriptObjects(queryKind, target, queryArg, args);
        if (objects.Count == 0)
            return bodyEnd + 1;

        // Source-X changes the default object, retaining the original trigger arguments.
        int iterations = 0;
        scope.LoopDepth++;
        try
        {
            foreach (var obj in objects)
            {
                if (iterations++ >= scope.MaxLoopIterations) break;
                result = Execute(body, obj, source, args, scope);
                if (scope.IsContinuing) { scope.IsContinuing = false; continue; }
                if (scope.IsBreaking) { scope.IsBreaking = false; break; }
                if (scope.IsReturning) break;
            }
        }
        finally { scope.LoopDepth--; }

        return bodyEnd + 1;
    }

    /// <summary>Build a human-readable "file(line) [trigger]" tag for the
    /// given script line, used by ExpressionParser.ReportUnresolved when
    /// scriptdebug is on. Falls back gracefully when only partial source
    /// info is present.</summary>
    private static string FormatSourceLabel(ScriptKey key, ScriptScope scope)
    {
        string file = key.SourceFile;
        if (file.Length > 0)
        {
            // Strip the long absolute prefix so the warning stays readable.
            // We keep the last two path segments which is enough to
            // disambiguate (e.g. "core/dialogs/admin/d_admin.scp").
            int slash = file.LastIndexOf('/', file.Length - 1);
            if (slash < 0) slash = file.LastIndexOf('\\', file.Length - 1);
            if (slash > 0)
            {
                int prevSlash = slash > 0
                    ? file.LastIndexOfAny(new[] { '/', '\\' }, slash - 1)
                    : -1;
                file = prevSlash >= 0 ? file[(prevSlash + 1)..] : file[(slash + 1)..];
            }
        }

        string trigger = scope?.TriggerName ?? "";
        if (file.Length == 0 && trigger.Length == 0) return "";
        if (trigger.Length == 0)
            return key.SourceLine > 0 ? $"{file}({key.SourceLine})" : file;
        return key.SourceLine > 0
            ? $"{file}({key.SourceLine}) {trigger}"
            : $"{file} {trigger}";
    }

    /// <summary>Expand resource text with the same context as its script body.</summary>
    public string ExpandText(string text, IScriptObj target, ITextConsole? source,
        ITriggerArgs? args, ScriptScope scope) => ResolveArgs(text, target, source, args, scope);

    private string ResolveArgs(string arg, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope = null)
    {
        if (string.IsNullOrEmpty(arg)) return "";
        if (arg.IndexOf('<') < 0) return arg;

        // Set resolver to target object for <property> lookups
        var saved = PushResolvers(target, source, args, scope);
        try { return _expr.EvaluateStr(arg); }
        finally { PopResolvers(saved); }
    }

    private long EvaluateWithResolver(string expr, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope = null)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return 0;
        var saved = PushResolvers(target, source, args, scope);
        try
        {
            return _expr.Evaluate(expr.AsSpan());
        }
        finally { PopResolvers(saved); }
    }

    /// <summary>Evaluate an IF/ELIF/WHILE condition with the target-bound
    /// resolvers. Uses the Source-X conditional model: top-level || and &amp;&amp;
    /// split the expression into subexpressions combined left-to-right with
    /// short-circuiting (equal precedence), each side folding right like any
    /// Sphere expression.</summary>
    private bool EvaluateConditionWithResolver(string expr, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope = null)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;
        var saved = PushResolvers(target, source, args, scope);
        try
        {
            return _expr.EvaluateConditional(expr);
        }
        finally { PopResolvers(saved); }
    }

    /// <summary>Public entry to evaluate a condition string against a target object,
    /// with SRC/target-bound variable and function resolution. Used by the skill-menu
    /// builder for TESTIF= entry gating (Source-X CClientUse TESTIF).</summary>
    public bool EvaluateConditionForTarget(string expr, IScriptObj target, ITextConsole? source)
        => EvaluateConditionWithResolver(expr, target, source, null, null);

    /// <summary>Evaluate with the target-bound resolvers, reporting whether the string
    /// was a genuine numeric expression (vs a string literal — e.g. a RETURN of a name
    /// or defname). Lets RETURN preserve string values instead of collapsing to 0.</summary>
    private bool TryEvaluateWithResolver(string expr, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope, out long value)
    {
        if (string.IsNullOrWhiteSpace(expr)) { value = 0; return false; }
        var saved = PushResolvers(target, source, args, scope);
        try
        {
            return _expr.TryEvaluate(expr.AsSpan(), out value);
        }
        finally { PopResolvers(saved); }
    }

    /// <summary>Evaluate a [FUNCTION] named by the whole token, with no arguments.
    /// TryResolveFunctionExpression exists for the &lt;name args&gt; form and declines a
    /// bare name; this is the bare one.</summary>
    private string? CallNoArgFunction(string name, IScriptObj target, ITextConsole? source,
        ITriggerArgs? args, ScriptScope? scope)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string text = name.Trim();
        if (!(char.IsLetter(text[0]) || text[0] == '_'))
            return null;

        if (ResolveFunctionExpressionWithScope != null && scope != null)
        {
            string? v = ResolveFunctionExpressionWithScope(text, "", target, source, args, scope);
            if (v != null) return v;
        }
        return ResolveFunctionExpression?.Invoke(text, "", target, source, args);
    }

    private string? TryResolveFunctionExpression(string expr, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope)
    {
        if (ResolveFunctionExpressionWithScope == null && ResolveFunctionExpression == null ||
            string.IsNullOrWhiteSpace(expr))
            return null;

        string text = expr.Trim();
        if (!(char.IsLetter(text[0]) || text[0] == '_'))
            return null;

        int nameLen = 1;
        while (nameLen < text.Length && (char.IsLetterOrDigit(text[nameLen]) || text[nameLen] == '_'))
            nameLen++;

        if (nameLen >= text.Length)
            return null;

        string funcName = text[..nameLen];
        string remainder = text[nameLen..].TrimStart();
        if (remainder.Length == 0)
            return null;

        string funcArgs;
        if (remainder[0] == '(')
        {
            int depth = 0;
            int angleDepth = 0;
            int end = -1;
            for (int i = 0; i < remainder.Length; i++)
            {
                char ch = remainder[i];
                if (ch == '<') angleDepth++;
                else if (ch == '>' && angleDepth > 0) angleDepth--;
                else if (angleDepth == 0)
                {
                    if (ch == '(') depth++;
                    else if (ch == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            end = i;
                            break;
                        }
                    }
                }
            }
            if (end <= 0)
                return null;
            funcArgs = remainder[1..end].Trim();
        }
        else
        {
            funcArgs = remainder.Trim();
        }

        return ResolveFunctionExpressionWithScope != null && scope != null
            ? ResolveFunctionExpressionWithScope(funcName, funcArgs, target, source, args, scope)
            : ResolveFunctionExpression?.Invoke(funcName, funcArgs, target, source, args);
    }

    private TriggerResult InvokeFunction(string funcName, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope scope)
    {
        if (CallFunctionWithScope != null)
            return CallFunctionWithScope(funcName, target, source, args, scope);

        return CallFunction?.Invoke(funcName, target, source, args) ?? TriggerResult.Default;
    }

    /// <summary>Host bridge for a Source-X object reference head — TOPOBJ, CONT, LINK
    /// and the rest of r_GetRef (CScriptObj.cpp:1217). The object layer owns those
    /// relationships, so the host wires this; SRC is resolved here, since the args
    /// already carry it. Null means the head names no reachable object.</summary>
    public Func<IScriptObj, string, IScriptObj?>? ResolveObjectRef { get; set; }

    /// <summary>Strip a reference head off a CALL and return the object the call
    /// belongs to, leaving <paramref name="head"/> holding just the function name.
    ///
    /// Source-X resolves the reference BEFORE looking the function up: Execute_Call
    /// runs r_GetRef over the argument and handles "SRC." itself through
    /// pSrc-&gt;GetChar (CScriptObj.cpp:1515). Taking the first word as a function name
    /// meant "CALL SRC.f_mark" looked for a function literally called "SRC.f_mark",
    /// found none, and the line vanished.</summary>
    private IScriptObj? ResolveCallReference(ref string head, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args)
    {
        int dot = head.IndexOf('.');
        if (dot <= 0)
            return target;

        string refHead = head[..dot];
        string rest = head[(dot + 1)..];
        if (rest.Length == 0)
            return target;

        if (refHead.Equals("SRC", StringComparison.OrdinalIgnoreCase))
        {
            var srcObj = args?.Source ?? source?.GetSourceChar();
            head = rest;
            return srcObj;
        }

        var resolved = ResolveObjectRef?.Invoke(target, refHead);
        if (resolved == null)
            return target;   // not a reference head after all: leave the name alone

        head = rest;
        return resolved;
    }

    private bool TryPreferredFunction(string name, string rawArgs, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope)
    {
        if (!target.PreferScriptFunction(name) || !FunctionExists(name) ||
            (CallFunctionWithScope == null && CallFunction == null))
            return false;
        var functionArgs = new TriggerArgs
        {
            Source = args?.Source,
            Object1 = args?.Object1,
            Object2 = args?.Object2,
        };
        functionArgs.InitFromRaw(rawArgs);
        InvokeFunction(name, target, source, functionArgs, scope);
        return true;
    }

    /// <summary>Run one verb LINE on <paramref name="target"/> in Source-X's r_Verb
    /// order: the verb table owns its names outright, an UNKNOWN name reaches the
    /// script [FUNCTION] (CObjBase.cpp:2134), and what neither claims becomes a
    /// property assignment through the default r_LoadVal branch (CScriptObj.cpp:1481).
    /// The console bridge sits between the verb and the function so host-side verbs
    /// (dialogs, targeting, SERV.*) keep their place.</summary>
    private void ExecuteVerbLine(string verb, string verbArgs, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope)
    {
        if (TryPreferredFunction(verb, verbArgs, target, source, args, scope))
            return;
        var console = source ?? NullConsole.Instance;
        if (target.TryExecuteCommand(verb, verbArgs, console, out bool nameOwned))
            return;
        if (nameOwned)
            return;   // the verb owns the name and declined; upstream stops here
        if (console.TryExecuteScriptCommand(target, verb, verbArgs, args))
            return;

        if (CallFunctionWithScope != null || CallFunction != null)
        {
            var funcArgs = new TriggerArgs
            {
                Source = args?.Source,
                Object1 = args?.Object1,
                Object2 = args?.Object2,
            };
            funcArgs.InitFromRaw(verbArgs);
            if (InvokeFunction(verb, target, source, funcArgs, scope) != TriggerResult.Default)
                return;
            if (FunctionExists(verb))
                return;
        }

        if (verbArgs.Length > 0)
            target.TrySetProperty(verb, EvalNumericArg(verb, verbArgs));
    }

    /// <summary>
    /// Work out an assignment's value when it is arithmetic and nothing else.
    ///
    /// Upstream loads a numeric key through the expression engine
    /// (CScript.cpp:154, GetArgVal -> Exp_GetVal), so "MORE2=&lt;MOREX&gt;/3" is a
    /// division, not the text "30/3". Only &lt;...&gt; substitution happened here, and
    /// the object parsed what was left as zero - silently, since a property set that
    /// stores 0 still reports success.
    ///
    /// Which keys are numeric is not knowable at this layer, so
    /// <see cref="ScriptArithmetic.IsPlainArithmetic"/> decides on the value instead:
    /// numbers, operators and brackets only. A text value is returned untouched, and so
    /// is anything the expression engine refuses.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(arg))]
    private string? EvalNumericArg(string? arg)
    {
        if (arg == null || !ScriptArithmetic.IsPlainArithmetic(arg)) return arg;
        return _expr.TryEvaluate(arg, out long value)
            ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : arg;
    }

    /// <summary>The same, for the keys that must keep their text whatever shape the
    /// value has. P is a coordinate list upstream parses itself (CPointBase::Read), and
    /// a bare "1,2" never reaches the arithmetic test - but "P=&lt;SRC.P&gt;" can
    /// resolve to a single number, and evaluating that would be a placement.</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(arg))]
    private string? EvalNumericArg(string key, string? arg) =>
        NonNumericKeys.Contains(key) ? arg : EvalNumericArg(arg);

    private static readonly HashSet<string> NonNumericKeys =
        new(StringComparer.OrdinalIgnoreCase) { "P", "POS", "NAME", "EVENTS", "TEVENTS", "ARGS" };

    /// <summary>Whether a script [FUNCTION] answers to this name. Lets the verb line
    /// tell "the function ran and returned nothing" apart from "there is no such
    /// function", so only the latter falls through to a property assignment.</summary>
    public Func<string, bool>? FunctionLookup { get; set; }

    private bool FunctionExists(string name) => FunctionLookup?.Invoke(name) ?? false;

    /// <summary>First segments that name a variable store or a namespace, never an
    /// object reference: a function name after them is not a call ON anything.</summary>
    private static readonly HashSet<string> NonReferenceHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "SRC", "DSRC", "SERV", "DEF", "DEF0", "TAG", "TAG0", "DTAG", "DTAG0", "CTAG", "CTAG0",
        "DCTAG", "DCTAG0", "LOCAL", "DLOCAL", "VAR", "VAR0", "DVAR", "FLOAT", "ARGV", "DB", "LDB",
        "FILE", "DEFMSG", "ARGS", "ARGN", "ARGN1", "ARGN2", "ARGN3", "ARGTXT", "ARGCHK",
    };

    /// <summary>&lt;REF1.f_x&gt;, &lt;ARGO.f_x&gt;, &lt;LINK.f_x&gt;, &lt;SRC.TARG.f_x&gt;, &lt;UID.x.f_x&gt; ...:
    /// a [FUNCTION] called ON the referenced object. Source-X r_WriteVal walks the
    /// reference (r_GetRef) and, when the object's own tables do not name the key,
    /// calls the function on it (CObjBase.cpp:971, CScriptObj.cpp:1481). Only SRC had
    /// that fallback here, so every other head read such a call as "0" - and a guard
    /// like IF (&lt;LINK.IsDeath&gt;) never saw a dead character.</summary>
    private string? TryResolveReferencedFunction(string varName, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope? scope)
    {
        int dot = varName.LastIndexOf('.');
        if (dot <= 0 || dot == varName.Length - 1 || ResolveObjectRef == null)
            return null;
        string fn = varName[(dot + 1)..];
        if (!(char.IsLetter(fn[0]) || fn[0] == '_'))
            return null;
        foreach (char c in fn)
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return null;
        if (!FunctionExists(fn))
            return null;

        var obj = ResolveReferencePrefix(varName[..dot], target, source, args, scope);
        if (obj == null)
            return null;

        // The object's own key wins over a same-named function, as upstream.
        if (obj.TryGetProperty(fn, out string own))
            return own;
        return CallNoArgFunction(fn, obj, source, args, scope);
    }

    /// <summary>The object a reference prefix (REF1, ARGO, LINK, SRC.TARG, UID.x,
    /// TOPOBJ ...) names, or null when it names none - Source-X r_GetRef.</summary>
    private IScriptObj? ResolveReferencePrefix(string prefix, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope? scope)
    {
        if (ResolveObjectRef == null || prefix.Length == 0)
            return null;
        int firstDot = prefix.IndexOf('.');
        string head = firstDot < 0 ? prefix : prefix[..firstDot];
        if (prefix.Equals("SRC", StringComparison.OrdinalIgnoreCase))
            return args?.Source ?? source?.GetSourceChar();
        if (NonReferenceHeads.Contains(head))
            return null;

        // UID.<x>, NEW, TOPOBJ, CONT, LINK...: the host names these directly;
        // anything else (REF1, ARGO, OBJ, SRC.TARG) reads as a uid first.
        var obj = firstDot < 0 || head.Equals("UID", StringComparison.OrdinalIgnoreCase)
            ? ResolveObjectRef(target, prefix)
            : null;
        if (obj != null)
            return obj;
        string? refValue = ResolveVarForTarget(prefix, target, source, args, scope);
        if (string.IsNullOrWhiteSpace(refValue) || refValue.Trim() is "0" or "-1")
            return null;
        return ResolveObjectRef(target, "UID." + refValue.Trim());
    }

    /// <summary>Source-X CHC_ISMYPET: NPC_IsOwnedBy(SRC, fAllowGM=true)
    /// (CCharNPCStatus.cpp:449) - asked of the NPC, answered about SRC.</summary>
    private static string IsMyPet(IScriptObj npc, IScriptObj? src)
    {
        static string Prop(IScriptObj o, string key) => o.TryGetProperty(key, out string v) ? v : "";
        static ulong Uid(string s) =>
            ulong.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s,
                System.Globalization.NumberStyles.HexNumber, null, out ulong v) ? v : 0;
        if (src == null || Prop(npc, "ISPLAYER") == "1")
            return "0";
        ulong npcUid = Uid(Prop(npc, "UID")), srcUid = Uid(Prop(src, "UID"));
        if (srcUid == 0)
            return "0";
        if (npcUid == srcUid)
            return "1";
        if (Prop(src, "GM") == "1" &&
            int.TryParse(Prop(src, "PRIVLEVEL"), out int srcPriv) &&
            int.TryParse(Prop(npc, "PRIVLEVEL"), out int npcPriv) && srcPriv > npcPriv)
            return "1";
        return Uid(Prop(npc, "NPCMASTER")) == srcUid ? "1" : "0";
    }

    private string? ResolveVarForTarget(string varName, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope = null)
    {
        if (varName.EndsWith(".ISMYPET", StringComparison.OrdinalIgnoreCase) &&
            ResolveReferencePrefix(varName[..^".ISMYPET".Length], target, source, args, scope) is { } petObj)
            return IsMyPet(petObj, args?.Source ?? source?.GetSourceChar());
        if (varName.IndexOf('.') > 0 &&
            TryResolveReferencedFunction(varName, target, source, args, scope) is { } referenced)
            return referenced;

        static string GetObjectRef(IScriptObj? obj)
        {
            if (obj == null)
                return "0";
            if (obj.TryGetProperty("UID", out string uidVal))
                return uidVal;
            return obj.GetName();
        }

        if (varName.Equals("ARGS", StringComparison.OrdinalIgnoreCase))
            return args?.ArgString ?? "";
        if (varName.Equals("SRC", StringComparison.OrdinalIgnoreCase))
            return GetObjectRef(args?.Source ?? source?.GetSourceChar());
        if (varName.Equals("ARGN1", StringComparison.OrdinalIgnoreCase) ||
            varName.Equals("ARGN", StringComparison.OrdinalIgnoreCase))
            return args?.Number1.ToString() ?? "0";
        if (varName.Equals("ARGN2", StringComparison.OrdinalIgnoreCase))
            return args?.Number2.ToString() ?? "0";
        if (varName.Equals("ARGN3", StringComparison.OrdinalIgnoreCase))
            return args?.Number3.ToString() ?? "0";
        if (varName.Equals("ARGO", StringComparison.OrdinalIgnoreCase))
            return args?.Object1 is not { } argo ? "0" :
                argo.TryGetProperty("UID", out string argoUid) ? argoUid : "1";
        if (varName.StartsWith("ARGO.", StringComparison.OrdinalIgnoreCase))
        {
            string subProp = varName[5..];
            if (args?.Object1 != null && args.Object1.TryGetProperty(subProp, out string objVal))
                return objVal;
            return "0";
        }
        if (varName.Equals("ACT", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("ACT.", StringComparison.OrdinalIgnoreCase))
        {
            // ACT belongs to the current character (CChar::r_GetRef / CHR_ACT),
            // not to the engine's secondary trigger object.
            return target.TryGetProperty(varName, out string actValue) ? actValue : "0";
        }
        // Source-X CHC_ISMYPET: is this char a pet owned by SRC? It needs the
        // caller context, so it resolves here — a plain Character property
        // read has no SRC to compare against.
        if (varName.Equals("ISMYPET", StringComparison.OrdinalIgnoreCase))
            return IsMyPet(target, args?.Source ?? source?.GetSourceChar());

        // LINK is the CURRENT object's link property (Source-X m_uidLink) — e.g. a
        // lever's linked door — NOT a trigger-arg object. Delegate to the target,
        // which resolves both <LINK> (the linked uid) and <LINK.prop> (the linked
        // object's property). Previously LINK collided with ACT (both returned
        // Object2), so <LINK> never reflected the object's actual link.
        if (varName.Equals("LINK", StringComparison.OrdinalIgnoreCase))
            return target.TryGetProperty("LINK", out string linkUid) && !string.IsNullOrEmpty(linkUid)
                ? linkUid
                : "0";
        if (varName.StartsWith("LINK.", StringComparison.OrdinalIgnoreCase))
            return target.TryGetProperty(varName, out string linkVal) ? linkVal : "0";
        if (varName.Equals("TARGP", StringComparison.OrdinalIgnoreCase))
        {
            if ((source ?? NullConsole.Instance).TryResolveScriptVariable(varName, target, args, out string targPoint))
                return targPoint;
            return args?.ArgString ?? "0,0,0,0";
        }
        if (varName.Equals("DARGV", StringComparison.OrdinalIgnoreCase))
        {
            return args is TriggerArgs triggerArgs
                ? triggerArgs.GetArgc().ToString()
                : "0";
        }
        if (varName.StartsWith("ARGV", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = (varName.Length > 4 ? varName[4..] : "").Trim();
            if (suffix.StartsWith("[", StringComparison.Ordinal) &&
                suffix.EndsWith("]", StringComparison.Ordinal) && suffix.Length > 2)
                suffix = suffix[1..^1].Trim();

            // Bare <ARGV> is the argument COUNT, not the first argument
            // (CScriptTriggerArgs.cpp:511: an empty key formats the quantity). A
            // pack's list helpers are built on it - `[FUNCTION ARRAYCOUNT] RETURN
            // <EVAL <ARGV>>`, and ARRAY reads the last field with
            // `<ARGV[<EVAL <ARGV> - 1>]>` - so returning argument zero made every
            // count come back as the first element, which evaluates to nothing.
            // A dialog looping `FOR 1 <ARRAYCOUNT <LOCAL.list>>` then ran
            // "FOR 1 0", which counts DOWN, and produced two rows of nothing.
            if (args == null || string.IsNullOrEmpty(args.ArgString))
                return suffix.Length == 0 ? "0" : "";

            IReadOnlyList<string> argv = args is TriggerArgs triggerArgs
                ? triggerArgs.GetArgv()
                : args.ArgString.Split(',', StringSplitOptions.TrimEntries);

            if (suffix.Length == 0)
                return argv.Count.ToString();

            // The index is an EXPRESSION, not a literal: upstream runs it through
            // Exp_GetUSingle (CScriptTriggerArgs.cpp:518). A pack's ARRAY helper
            // relies on it - `<ARGV[<DLOCAL.TEMP>]>` where TEMP holds "2 -1" - and a
            // plain integer parse simply fails on that and returns nothing.
            int idx;
            if (!int.TryParse(suffix, out idx))
            {
                try { idx = (int)_expr.Evaluate(suffix); }
                catch (Exception) { return ""; }
            }
            return idx >= 0 && idx < argv.Count ? argv[idx] : "";
        }

        // LOCAL.varname / DLOCAL.varname — read from scope local variables.
        // DLOCAL shares storage with LOCAL; the "d" prefix only signals
        // the reader wants a decimal interpretation (our numeric coercion
        // handles that uniformly on the consumer side).
        if (varName.StartsWith("LOCAL.", StringComparison.OrdinalIgnoreCase))
        {
            string localName = varName[6..];
            if (scope != null)
                return scope.LocalVars.Get(localName) ?? "0";
            return "0";
        }
        if (varName.StartsWith("DLOCAL.", StringComparison.OrdinalIgnoreCase))
        {
            string localName = varName[7..];
            if (scope != null)
                return scope.LocalVars.Get(localName) ?? "0";
            return "0";
        }

        // REFn / REFn.property — local object references
        if (varName.StartsWith("REF", StringComparison.OrdinalIgnoreCase) && varName.Length > 3 && char.IsDigit(varName[3]))
        {
            int dotIdx = varName.IndexOf('.');
            if (dotIdx < 0)
            {
                // <REFn> — return UID string
                if (int.TryParse(varName.AsSpan(3), out int refIdx) && scope != null)
                    return scope.GetRef(refIdx);
                return "0";
            }
            // <REFn.property> — resolve property on referenced object
            if (int.TryParse(varName.AsSpan(3, dotIdx - 3), out int refIdx2) && scope != null)
            {
                string refUid = scope.GetRef(refIdx2);
                string subProp = varName[(dotIdx + 1)..];
                // Resolve via server property resolver to find the object
                string? refVal = ServerPropertyResolver?.Invoke($"_REF_GET={refUid}|{subProp}");
                return refVal ?? "0";
            }
            return "0";
        }

        // FLOAT.name — local float variables
        if (varName.StartsWith("FLOAT.", StringComparison.OrdinalIgnoreCase))
        {
            string floatName = varName[6..];
            if (scope != null)
                return scope.GetFloat(floatName);
            return "0.0";
        }

        // SRC.property — read from source object. DSRC.property is the
        // decimal-forced variant (Sphere convention): reads the same
        // property but signals the consumer wants a plain number
        // without leading '0' hex prefix. Storage is shared; we just
        // accept both prefixes and return the raw tag value.
        if (varName.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DSRC.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = varName.IndexOf('.');
            string subProp = varName[(dot + 1)..];
            if (args?.Source == null)
                return "0";
            if (args.Source.TryGetProperty(subProp, out string srcVal))
                return srcVal;

            // A key the object's own table does not name is a [FUNCTION] called ON it,
            // which is what r_WriteVal does after the property tables miss
            // (CObjBase.cpp:971, CScriptObj.cpp:1481). Reading SRC.<name> as a property
            // and nothing else meant a function reached through SRC was never called -
            // and the packs reach for them that way constantly. The clothing gate asks
            // !<SRC.f_isHuman>, so every human was refused every garment.
            if (!subProp.Contains('.', StringComparison.Ordinal) &&
                !subProp.Contains('(', StringComparison.Ordinal))
            {
                string? fnVal = CallNoArgFunction(subProp, args.Source, source, args, scope);
                if (fnVal != null)
                    return fnVal;
            }
            return "0";
        }

        // REGION / REGION.property — current region reference (resolved via ServerPropertyResolver)
        if (varName.Equals("REGION", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("REGION.", StringComparison.OrdinalIgnoreCase))
        {
            if (target.TryGetProperty("REGION", out string regionUid))
            {
                if (varName.Equals("REGION", StringComparison.OrdinalIgnoreCase))
                    return regionUid;
                // REGION.property — resolve via ServerPropertyResolver
                string? regionVal = ServerPropertyResolver?.Invoke($"_REGION_GET={regionUid}|{varName[7..]}");
                return regionVal ?? "0";
            }
            return "0";
        }

        // ROOM / ROOM.property — current room reference (resolved via ServerPropertyResolver)
        if (varName.Equals("ROOM", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("ROOM.", StringComparison.OrdinalIgnoreCase))
        {
            // First try to resolve from target (character's ROOM tag)
            if (target.TryGetProperty("ROOM", out string roomUid))
            {
                if (varName.Equals("ROOM", StringComparison.OrdinalIgnoreCase))
                    return roomUid;
                // ROOM.property — resolve via ServerPropertyResolver
                string? roomVal = ServerPropertyResolver?.Invoke($"_ROOM_GET={roomUid}|{varName[5..]}");
                return roomVal ?? "0";
            }
            return "0";
        }

        // TYPEDEF / TYPEDEF.property — the object's own base definition.
        //
        // Upstream lists it on CObjBase beside ROOM, SECTOR, TOPOBJ and SPAWNITEM
        // (sm_szRefKeys, CObjBase.cpp:899) and resolves it to Base_GetDef(), so
        // <TYPEDEF.TDATA1> asks the ITEMDEF or CHARDEF this thing was made from
        // rather than the thing itself. Nothing answered it here, so every such read
        // came back as the unresolved "0".
        //
        // It goes through the host for the same reason ROOM does: the definition
        // tables and the reader for their fields already live there, and a second
        // reader would only be something for the first one to disagree with.
        if (varName.Equals("TYPEDEF", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("TYPEDEF.", StringComparison.OrdinalIgnoreCase))
        {
            if (varName.Length == 7)
            {
                // The head on its own: what the object already answers, which is the
                // definition's name for a character and its defname for an item.
                return target.TryGetProperty("TYPEDEF", out string ownDef) ? ownDef : "0";
            }
            if (target.TryGetProperty("UID", out string defUid))
            {
                string? defVal = ServerPropertyResolver?.Invoke(
                    $"_TYPEDEF_GET={defUid}|{varName[8..]}");
                if (!string.IsNullOrEmpty(defVal)) return defVal;
            }
            return "0";
        }

        // SERV.* — server-level property resolution
        if (varName.StartsWith("SERV.", StringComparison.OrdinalIgnoreCase))
        {
            string servProp = varName[5..];
            string? servVal = ServerPropertyResolver?.Invoke(servProp);
            if (servVal != null) return servVal;

            // Upstream, a key the server does not answer is tried as a [FUNCTION]
            // before anything else (CServerDef.cpp:509 - r_GetFunctionIndex on the
            // remaining key, then the parent's own table). Returning "0" here instead
            // meant the resolution ENDED at the server, and a pack function meant to
            // answer a SERV read could never run - the live pack's date line reads
            // <SERV.DAYNAME>, which its own [FUNCTION SERV.DAYNAME] exists to answer,
            // and got "0". Both spellings are tried: the remainder as upstream does,
            // and the whole token, which is how the pack spells the section header.
            // The read carries no arguments, which TryResolveFunctionExpression does
            // not handle (it splits a name from an argument list), so call straight
            // through with an empty argument string.
            string? fnVal = CallNoArgFunction(servProp, target, source, args, scope)
                         ?? CallNoArgFunction(varName, target, source, args, scope);
            if (fnVal != null) return fnVal;

            return "0";
        }

        if (varName.StartsWith("DB.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("LDB.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("MDB.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("FILE.", StringComparison.OrdinalIgnoreCase))
        {
            return ServerPropertyResolver?.Invoke(varName) ?? "0";
        }

        // DEF.X / DEF0.X — generic [DEFNAME] lookup. Dialog rendering had
        // its own resolver for these, but functions such as admin /
        // f_Admin_GetPlayers run through the interpreter and need the same
        // Source-X surface.
        if (varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DEF0.", StringComparison.OrdinalIgnoreCase))
        {
            string? v = ServerPropertyResolver?.Invoke(varName);
            if (v != null) return v;

            int dot = varName.IndexOf('.');
            if (dot >= 0 && dot + 1 < varName.Length)
            {
                v = ServerPropertyResolver?.Invoke(varName[(dot + 1)..]);
                if (v != null) return v;
            }

            return "0";
        }

        // Standalone server properties: RTIME, RTICKS, RTIME.FORMAT, RTICKS.FORMAT, RTICKS.FROMTIME
        if (varName.StartsWith("RTIME", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("RTICKS", StringComparison.OrdinalIgnoreCase))
        {
            string? servVal = ServerPropertyResolver?.Invoke(varName);
            if (servVal != null) return servVal;
            return "0";
        }

        // VAR.* / VAR0.* — global server variables
        if (varName.StartsWith("VAR0.", StringComparison.OrdinalIgnoreCase))
        {
            string? v = ServerPropertyResolver?.Invoke(varName);
            return v ?? "0";
        }
        if (varName.StartsWith("VAR.", StringComparison.OrdinalIgnoreCase))
        {
            string? v = ServerPropertyResolver?.Invoke(varName);
            return v ?? "";
        }

        // OBJ / OBJ.property — global object reference
        if (varName.Equals("OBJ", StringComparison.OrdinalIgnoreCase))
        {
            string? v = ServerPropertyResolver?.Invoke("OBJ");
            return v ?? "0";
        }
        if (varName.StartsWith("OBJ.", StringComparison.OrdinalIgnoreCase))
        {
            string? v = ServerPropertyResolver?.Invoke(varName);
            return v ?? "0";
        }

        // NEW / NEW.property — last created object reference
        if (varName.Equals("NEW", StringComparison.OrdinalIgnoreCase))
        {
            string? v = ServerPropertyResolver?.Invoke("NEW");
            return v ?? "0";
        }
        if (varName.StartsWith("NEW.", StringComparison.OrdinalIgnoreCase))
        {
            string? v = ServerPropertyResolver?.Invoke(varName);
            return v ?? "0";
        }

        // UID.0xHEX.property — direct object access by UID
        if (varName.StartsWith("UID.", StringComparison.OrdinalIgnoreCase) && varName.Length > 4)
        {
            string? v = ServerPropertyResolver?.Invoke(varName);
            if (v != null) return v;
        }

        // GETREFTYPE — object type code. Values match Source-X
        // [DEFNAME ref_types] flags so script comparisons like
        // <If (<GetRefType> == <Def.TRef_Char>)> work without extra
        // tweaks. Bit layout (hex):
        //   tref_serv     0x000001
        //   tref_account  0x000200
        //   tref_world    0x004000
        //   tref_client   0x010000
        //   tref_object   0x020000
        //   tref_char     0x040000
        //   tref_item     0x080000
        if (varName.Equals("GETREFTYPE", StringComparison.OrdinalIgnoreCase))
        {
            if (target.TryGetProperty("ISCHAR", out string isChar) && isChar == "1")
                return "0" + 0x040000.ToString("X");
            if (target.TryGetProperty("ISITEM", out string isItem) && isItem == "1")
                return "0" + 0x080000.ToString("X");
            // Fallback when target isn't a tangible object — treat as the
            // server context, matching SPHERESCRIPT behaviour for verb
            // sources like CONSOLE/SERVER.
            return "0" + 0x000001.ToString("X");
        }

        // DEFMSG.* — server default messages
        if (varName.StartsWith("DEFMSG.", StringComparison.OrdinalIgnoreCase))
        {
            string? v = ServerPropertyResolver?.Invoke(varName);
            return v ?? "";
        }

        // DISTANCE — tile-distance from args.Source to the target. Sphere
        // script convention: on an item's @DClick / @Step block,
        // <distance> is the range between the player (src) and the
        // object the trigger fired on. Neither side carries the other's
        // position, so the resolver has to combine target + args.Source
        // here rather than inside their respective TryGetProperty.
        if (varName.Equals("DISTANCE", StringComparison.OrdinalIgnoreCase))
        {
            if (args?.Source != null &&
                args.Source.TryGetProperty("X", out string srcXs) && int.TryParse(srcXs, out int srcX) &&
                args.Source.TryGetProperty("Y", out string srcYs) && int.TryParse(srcYs, out int srcY) &&
                target.TryGetProperty("X", out string tgtXs) && int.TryParse(tgtXs, out int tgtX) &&
                target.TryGetProperty("Y", out string tgtYs) && int.TryParse(tgtYs, out int tgtY))
            {
                int dx = Math.Abs(srcX - tgtX);
                int dy = Math.Abs(srcY - tgtY);
                return Math.Max(dx, dy).ToString(); // Chebyshev (UO range)
            }
            return "0";
        }

        if (target.TryGetProperty(varName, out string value))
            return value;
        if ((source ?? NullConsole.Instance).TryResolveScriptVariable(varName, target, args, out string resolved))
            return resolved;

        // A [FUNCTION] named by the whole token, called with no arguments.
        //
        // Upstream tries this inside r_WriteVal itself: a key the object's own table
        // does not name is looked up with r_GetFunctionIndex and CALLED
        // (CObjBase.cpp:971, CScriptObj.cpp:1481), before anything treats the word as
        // a constant. Only the SERV branch did that here, so a bare <f_something> fell
        // straight through to the defname fallback below - and a FUNCTION *is* a named
        // resource, so the fallback answered with its resource index. The read did not
        // just fail: it came back as a large non-zero number, which is TRUE. Every
        // guard written as IF (<f_isHuman>) passed, and every one written as
        // IF !(<f_isHuman>) failed, whoever was asking.
        if (!varName.Contains('.', StringComparison.Ordinal) &&
            !varName.Contains('(', StringComparison.Ordinal))
        {
            string? fnVal = CallNoArgFunction(varName, target, source, args, scope);
            if (fnVal != null)
                return fnVal;
        }

        // Bare defname/constant fallback (e.g. <statf_insubstantial>,
        // <memory_ipet>) via the shared server resolver. Source scripts use
        // these names without DEF./DEF0. prefixes inside expressions.
        if (!varName.Contains('.', StringComparison.Ordinal) &&
            !varName.Contains('(', StringComparison.Ordinal) &&
            !varName.Contains(')', StringComparison.Ordinal))
        {
            string? constVal = ServerPropertyResolver?.Invoke(varName);
            if (constVal != null)
                return constVal;
        }

        return null;
    }

    private void ParseLocalAssignment(string arg, ScriptScope scope)
    {
        int dotIdx = arg.IndexOf('.');
        if (dotIdx < 0) return;

        string rest = arg[(dotIdx + 1)..];
        int eqIdx = rest.IndexOf('=');
        if (eqIdx < 0)
        {
            scope.LocalVars.Set(rest, "");
            return;
        }

        string varName = rest[..eqIdx].Trim();
        string varVal = rest[(eqIdx + 1)..].Trim();
        scope.LocalVars.Set(varName, varVal);
    }

    private static int FindBlockEnd(IReadOnlyList<ScriptKey> lines, int start, string endKeyword)
    {
        int depth = 1;

        for (int i = start; i < lines.Count; i++)
        {
            string cmd = lines[i].KeyUpper;

            bool isOpener = endKeyword switch
            {
                "ENDFOR" => cmd == "FOR" || IsForVariant(cmd),
                "ENDWHILE" => cmd == "WHILE",
                "END" => cmd == "BEGIN",
                "ENDDO" => cmd is "DORAND" or "DOSWITCH",
                _ => false
            };

            if (isOpener)
                depth++;
            if (cmd == endKeyword)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return lines.Count;
    }

    private static int SkipBlock(IReadOnlyList<ScriptKey> lines, int idx, string cmd)
    {
        if (IsForVariant(cmd))
            cmd = "FOR";

        switch (cmd)
        {
            case "IF":
            {
                int depth = 1;
                idx++;
                while (idx < lines.Count && depth > 0)
                {
                    string c = lines[idx].KeyUpper;
                    if (c == "IF") depth++;
                    if (c == "ENDIF") depth--;
                    if (depth > 0) idx++;
                }
                return idx + 1;
            }
            case "FOR":
            {
                int depth = 1;
                idx++;
                while (idx < lines.Count && depth > 0)
                {
                    string c = lines[idx].KeyUpper;
                    if (c == "FOR" || IsForVariant(c))
                        depth++;
                    if (c == "ENDFOR") depth--;
                    if (depth > 0) idx++;
                }
                return idx + 1;
            }
            case "WHILE":
            {
                int depth = 1;
                idx++;
                while (idx < lines.Count && depth > 0)
                {
                    string c = lines[idx].KeyUpper;
                    if (c == "WHILE") depth++;
                    if (c == "ENDWHILE") depth--;
                    if (depth > 0) idx++;
                }
                return idx + 1;
            }
            case "DORAND":
            case "DOSWITCH":
            {
                int depth = 1;
                idx++;
                while (idx < lines.Count && depth > 0)
                {
                    string c = lines[idx].KeyUpper;
                    if (c == "DORAND" || c == "DOSWITCH") depth++;
                    if (c == "ENDDO") depth--;
                    if (depth > 0) idx++;
                }
                return idx + 1;
            }
            case "BEGIN":
            {
                int depth = 1;
                idx++;
                while (idx < lines.Count && depth > 0)
                {
                    string c = lines[idx].KeyUpper;
                    if (c == "BEGIN") depth++;
                    if (c == "END") depth--;
                    if (depth > 0) idx++;
                }
                return idx + 1;
            }
            default:
                return idx + 1;
        }
    }

    /// <summary>The loops that walk objects rather than a number range. Every one of
    /// them opens a block that ENDFOR closes, so the dispatcher, the block-end search
    /// and the skip path all have to agree on the list - and they did not:
    /// FORCHARLAYER and FORCHARMEMORYTYPE were dispatched but absent from the other
    /// two, so an ENDFOR belonging to one of them closed the loop AROUND it. The outer
    /// loop's body ended early and its tail ran once, outside the loop.</summary>
    private static bool IsForVariant(string cmd) =>
        cmd is "FORPLAYERS" or "FORCHARS" or "FORITEMS" or "FORCLIENTS"
            or "FOROBJS" or "FORINSTANCES" or "FORCONT" or "FORCONTID" or "FORCONTTYPE"
        or "FORCHARLAYER" or "FORCHARMEMORYTYPE" or "FORTIMERF";

    private static IReadOnlyList<ScriptKey> GetSubList(IReadOnlyList<ScriptKey> lines, int start, int end)
    {
        if (start >= end || start >= lines.Count) return [];
        return new LineSlice(lines, start, Math.Min(end, lines.Count) - start);
    }

    /// <summary>A window onto an already-parsed body rather than a copy of it.
    ///
    /// Loop bodies were copied into a fresh ScriptKey[] every time a block ran -
    /// and for WHILE / numeric FOR, once per ITERATION, so a 20-line body inside a
    /// 100-iteration loop copied 2,000 references for no reason. Execute only ever
    /// indexes and reads Count, so a view is enough; the enumerator is here for
    /// completeness and is not on any hot path.</summary>
    private sealed class LineSlice(IReadOnlyList<ScriptKey> source, int start, int count)
        : IReadOnlyList<ScriptKey>
    {
        public int Count => count;
        public ScriptKey this[int index] => source[start + index];
        public IEnumerator<ScriptKey> GetEnumerator()
        {
            for (int i = 0; i < count; i++) yield return source[start + i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class NullConsole : ITextConsole
    {
        public static readonly NullConsole Instance = new();
        public PrivLevel GetPrivLevel() => PrivLevel.Guest;
        public void SysMessage(string text) { }
        public string GetName() => "SYSTEM";
    }

}
