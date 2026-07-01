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

    public ScriptInterpreter(ExpressionParser expr, ILogger<ScriptInterpreter> logger)
    {
        _expr = expr;
        _logger = logger;
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
            using var __srcLabel = !string.IsNullOrEmpty(key.SourceFile)
                ? _expr.PushSourceLabel(FormatSourceLabel(key, scope))
                : default;

            string cmd = key.Key.ToUpperInvariant();

            // Handle LOCAL.varname=value (Key="LOCAL.foo", Arg="bar")
            if (cmd.StartsWith("LOCAL.", StringComparison.Ordinal))
            {
                string localExpr = cmd[6..] + (key.HasArg ? "=" + ResolveArgs(key.Arg, target, source, args, scope) : "");
                int eqIdx = localExpr.IndexOf('=');
                if (eqIdx > 0)
                {
                    string varName = localExpr[..eqIdx].Trim();
                    string varVal = localExpr[(eqIdx + 1)..].Trim();
                    scope.LocalVars.Set(varName, varVal);
                }
                i++;
                continue;
            }

            // Handle ARGN1 / ARGN2 / ARGN3 (bare ARGN == ARGN1) assignment — a script
            // mutates the trigger's numeric args (Source-X @Hit damage, @DropOn drop-Z,
            // @NPCActFight dist/motivation). TriggerDispatcher.RunWrapped copies these
            // back into the caller's TriggerArgs after the block. Without this the
            // assignment fell through to a no-op TrySetProperty, so ARGN was read-only.
            if (key.HasArg && args is TriggerArgs argnTarget &&
                cmd is "ARGN" or "ARGN1" or "ARGN2" or "ARGN3")
            {
                string rawVal = ResolveArgs(key.Arg, target, source, args, scope).Trim();
                if (TryParseArgN(rawVal, out int argnVal))
                {
                    if (cmd == "ARGN2") argnTarget.Number2 = argnVal;
                    else if (cmd == "ARGN3") argnTarget.Number3 = argnVal;
                    else argnTarget.Number1 = argnVal;
                }
                i++;
                continue;
            }

            // Handle ARGS assignment — a script rewrites the trigger's string argument
            // (Source-X @Speech text rewrite, @DropOn item filter). Like ARGN, the
            // assignment used to fall through to a no-op; RunWrapped copies it back.
            if (key.HasArg && args is TriggerArgs argsTarget && cmd == "ARGS")
            {
                argsTarget.ArgString = ResolveArgs(key.Arg, target, source, args, scope);
                i++;
                continue;
            }

            // Handle REFn=value (Key="REF1", Arg="<UID>")
            if (cmd.StartsWith("REF", StringComparison.Ordinal) && cmd.Length > 3 &&
                char.IsDigit(cmd[3]) && !cmd.Contains('.'))
            {
                if (int.TryParse(cmd.AsSpan(3), out int refIdx) && key.HasArg)
                {
                    string refVal = ResolveArgs(key.Arg, target, source, args, scope);
                    scope.SetRef(refIdx, refVal);
                }
                i++;
                continue;
            }

            // Handle REFn.property=value — set property on referenced object
            if (cmd.StartsWith("REF", StringComparison.Ordinal) && cmd.Length > 3 && char.IsDigit(cmd[3]))
            {
                int dotIdx = cmd.IndexOf('.');
                if (dotIdx > 3 && int.TryParse(cmd.AsSpan(3, dotIdx - 3), out int refIdx2))
                {
                    string refUid = scope.GetRef(refIdx2);
                    string subCmd = cmd[(dotIdx + 1)..];
                    string resolvedVal = ResolveArgs(key.Arg, target, source, args, scope);
                    // Resolve ref to object and set property or execute command
                    ServerPropertyResolver?.Invoke($"_REF_EXEC={refUid}|{subCmd}|{resolvedVal}");
                }
                i++;
                continue;
            }

            // Handle FLOAT.name=value
            if (cmd.StartsWith("FLOAT.", StringComparison.OrdinalIgnoreCase))
            {
                string floatName = cmd[6..];
                if (key.HasArg)
                {
                    string floatVal = ResolveArgs(key.Arg, target, source, args, scope);
                    scope.SetFloat(floatName, floatVal);
                }
                i++;
                continue;
            }

            switch (cmd)
            {
                case "IF":
                    i = ExecuteIf(lines, i, target, source, args, scope, out result);
                    break;

                case "FOR":
                    i = ExecuteFor(lines, i, target, source, args, scope, out result);
                    break;

                case "WHILE":
                    i = ExecuteWhile(lines, i, target, source, args, scope, out result);
                    break;

                case "DORAND":
                    i = ExecuteDoRand(lines, i, target, source, args, scope, out result);
                    break;

                case "DOSWITCH":
                    i = ExecuteDoSwitch(lines, i, target, source, args, scope, out result);
                    break;

                case "BEGIN":
                    i = ExecuteBegin(lines, i, target, source, args, scope, out result);
                    break;

                case "FORPLAYERS":
                case "FORINSTANCES":
                case "FORCHARS":
                case "FORCLIENTS":
                case "FORITEMS":
                case "FOROBJS":
                case "FORCONT":
                case "FORCONTID":
                case "FORCONTTYPE":
                case "FORCHARLAYER":
                case "FORCHARMEMORYTYPE":
                    i = ExecuteForObjects(lines, i, target, source, args, scope, out result, cmd);
                    break;

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
                {
                    string argStr = ResolveArgs(key.Arg, target, source, args, scope);
                    bool numeric = TryEvaluateWithResolver(argStr, target, source, args, scope, out long val);
                    // A numeric RETURN stores the evaluated number; a string RETURN (a
                    // [FUNCTION] returning a name/defname/message) keeps its text.
                    scope.ReturnValue = numeric ? val.ToString() : argStr;
                    scope.IsReturning = true;
                    result = val != 0 ? TriggerResult.True : TriggerResult.Default;
                    i = lines.Count;
                    break;
                }

                case "CALL":
                {
                    string funcName = ResolveArgs(key.Arg, target, source, args, scope).Trim();
                    if (!string.IsNullOrEmpty(funcName))
                    {
                        var callResult = InvokeFunction(funcName, target, source, args, scope);
                        if (callResult == TriggerResult.True)
                            result = TriggerResult.True;
                    }
                    i++;
                    break;
                }

                // TRY — execute the rest of the line as a normal command (obsolete in Source-X but kept for compat)
                case "TRY":
                {
                    string tryLine = ResolveArgs(key.Arg, target, source, args, scope);
                    if (!string.IsNullOrWhiteSpace(tryLine))
                    {
                        // Parse "property=value" or "command args" from the resolved line
                        int eqIdx = tryLine.IndexOf('=');
                        if (eqIdx > 0)
                        {
                            string prop = tryLine[..eqIdx].Trim();
                            string val = tryLine[(eqIdx + 1)..].Trim();
                            if (!target.TrySetProperty(prop, val))
                                target.TryExecuteCommand(prop, val, source ?? NullConsole.Instance);
                        }
                        else
                        {
                            int spIdx = tryLine.IndexOf(' ');
                            string verb = spIdx > 0 ? tryLine[..spIdx].Trim() : tryLine.Trim();
                            string verbArgs = spIdx > 0 ? tryLine[(spIdx + 1)..].Trim() : "";
                            if (!target.TryExecuteCommand(verb, verbArgs, source ?? NullConsole.Instance))
                                (source ?? NullConsole.Instance).TryExecuteScriptCommand(target, verb, verbArgs, args);
                        }
                    }
                    i++;
                    break;
                }

                // TRYSRV — execute with SERV as source context (PLEVEL 7)
                case "TRYSRV":
                {
                    string srvLine = ResolveArgs(key.Arg, target, source, args, scope);
                    if (!string.IsNullOrWhiteSpace(srvLine))
                    {
                        int spIdx = srvLine.IndexOf(' ');
                        string verb = spIdx > 0 ? srvLine[..spIdx].Trim() : srvLine.Trim();
                        string verbArgs = spIdx > 0 ? srvLine[(spIdx + 1)..].Trim() : "";
                        // Execute with SERVER privilege (Source-X g_Serv / PLEVEL 7) so the
                        // verb bypasses the original source's PLEVEL restriction — the whole
                        // point of TRYSRV. Previously it passed the original source (a
                        // low-plevel player, or Guest when null), so a privileged verb still
                        // failed. The original source's script bridge stays as the fallback
                        // so client-scoped verbs can still reach a client.
                        if (!target.TryExecuteCommand(verb, verbArgs, ServerConsole.Instance))
                            (source ?? NullConsole.Instance).TryExecuteScriptCommand(target, verb, verbArgs, args);
                    }
                    i++;
                    break;
                }

                // TRYSRC <srcRef> <verb args...> — execute the verb on the
                // referenced source object. Common pattern:
                //   TRYSRC <UID> DIALOGCLOSE d_spawn
                // If srcRef can be resolved, route through _REF_EXEC so
                // object verbs and client-scoped verbs (DIALOG/SDIALOG/...)
                // follow the same bridge. Fall back to legacy behaviour when
                // the first token isn't a source reference.
                case "TRYSRC":
                {
                    string srcLine = ResolveArgs(key.Arg, target, source, args, scope);
                    if (string.IsNullOrWhiteSpace(srcLine))
                    {
                        i++;
                        break;
                    }

                    string work = srcLine.Trim();
                    int firstSpace = work.IndexOf(' ');
                    if (firstSpace > 0)
                    {
                        string srcRef = work[..firstSpace].Trim();
                        string rest = work[(firstSpace + 1)..].Trim();
                        if (!string.IsNullOrWhiteSpace(srcRef) && !string.IsNullOrWhiteSpace(rest))
                        {
                            int cmdSpace = rest.IndexOf(' ');
                            string trysrcVerb = cmdSpace > 0 ? rest[..cmdSpace].Trim() : rest;
                            string trysrcArgs = cmdSpace > 0 ? rest[(cmdSpace + 1)..].Trim() : "";
                            if (!string.IsNullOrWhiteSpace(trysrcVerb))
                            {
                                ServerPropertyResolver?.Invoke($"_REF_EXEC={srcRef}|{trysrcVerb}|{trysrcArgs}");
                                i++;
                                break;
                            }
                        }
                    }

                    // Legacy fallback: execute payload directly on current target.
                    int spIdx = work.IndexOf(' ');
                    string verb = spIdx > 0 ? work[..spIdx].Trim() : work.Trim();
                    string verbArgs = spIdx > 0 ? work[(spIdx + 1)..].Trim() : "";
                    if (!target.TryExecuteCommand(verb, verbArgs, source ?? NullConsole.Instance))
                        (source ?? NullConsole.Instance).TryExecuteScriptCommand(target, verb, verbArgs, args);
                    i++;
                    break;
                }

                // TRYP <plevel> <verb args...> — Source-X CObjBase.cpp:2869.
                // First token is the minimum PrivLevel required; if SRC's
                // PLEVEL is lower we abort with a SysMessage. Otherwise the
                // remainder is executed exactly like TRY (verb on target).
                // Used by every standard property-edit dialog button:
                //   ON=100   TRYP 4 INPDLG BODY 30
                case "TRYP":
                {
                    string trypLine = ResolveArgs(key.Arg, target, source, args, scope);
                    if (string.IsNullOrWhiteSpace(trypLine))
                    {
                        i++;
                        break;
                    }

                    int firstSpace = trypLine.IndexOf(' ');
                    string plevelTok = firstSpace > 0 ? trypLine[..firstSpace].Trim() : trypLine.Trim();
                    string rest = firstSpace > 0 ? trypLine[(firstSpace + 1)..].Trim() : "";

                    if (!int.TryParse(plevelTok, out int minPlevel))
                    {
                        _logger.LogWarning("TRYP: invalid plevel token '{Tok}'", plevelTok);
                        i++;
                        break;
                    }

                    var actor = source ?? NullConsole.Instance;
                    if ((int)actor.GetPrivLevel() < minPlevel)
                    {
                        actor.SysMessage($"You lack the privilege to change the {rest} property.");
                        i++;
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(rest))
                    {
                        i++;
                        break;
                    }

                    int eqIdx = rest.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        string prop = rest[..eqIdx].Trim();
                        string val = rest[(eqIdx + 1)..].Trim();
                        if (!target.TrySetProperty(prop, val))
                            target.TryExecuteCommand(prop, val, actor);
                    }
                    else
                    {
                        int spIdx2 = rest.IndexOf(' ');
                        string trypVerb = spIdx2 > 0 ? rest[..spIdx2].Trim() : rest.Trim();
                        string trypVerbArgs = spIdx2 > 0 ? rest[(spIdx2 + 1)..].Trim() : "";
                        if (!target.TryExecuteCommand(trypVerb, trypVerbArgs, actor))
                            actor.TryExecuteScriptCommand(target, trypVerb, trypVerbArgs, args);
                    }
                    i++;
                    break;
                }

                // TRYLEVEL <plevel> <verb args...> — same as TRY but fail closed
                // when there is no concrete source to authorize.
                case "TRYLEVEL":
                {
                    string tryLevelLine = ResolveArgs(key.Arg, target, source, args, scope);
                    int firstSpace = tryLevelLine.IndexOf(' ');
                    string plevelTok = firstSpace > 0 ? tryLevelLine[..firstSpace].Trim() : tryLevelLine.Trim();
                    string rest = firstSpace > 0 ? tryLevelLine[(firstSpace + 1)..].Trim() : "";
                    if (!int.TryParse(plevelTok, out int minPlevel) ||
                        string.IsNullOrWhiteSpace(rest) ||
                        source == null ||
                        (int)(source?.GetPrivLevel() ?? PrivLevel.Guest) < minPlevel)
                    {
                        i++;
                        break;
                    }

                    int spIdx = rest.IndexOf(' ');
                    string verb = spIdx > 0 ? rest[..spIdx].Trim() : rest.Trim();
                    string verbArgs = spIdx > 0 ? rest[(spIdx + 1)..].Trim() : "";
                    var actor = source!;
                    if (!target.TryExecuteCommand(verb, verbArgs, actor))
                        actor.TryExecuteScriptCommand(target, verb, verbArgs, args);
                    i++;
                    break;
                }

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

        // Handle SRC. prefix — redirect to source object
        if (cmd.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase))
        {
            string subCmd = cmd[4..];
            IScriptObj? srcObj = args?.Source;
            if (srcObj != null)
            {
                if (key.HasArg && srcObj.TrySetProperty(subCmd, resolvedArg))
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
                    var funcArgs = new TriggerArgs
                    {
                        Source = args?.Source,
                        Object1 = args?.Object1,
                        Object2 = args?.Object2,
                        Number1 = args?.Number1 ?? 0,
                        Number2 = args?.Number2 ?? 0,
                        Number3 = args?.Number3 ?? 0,
                        ArgString = resolvedArg
                    };
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

        if (cmd.Equals("SERV.WRITEFILE", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("WRITEFILE", StringComparison.OrdinalIgnoreCase))
        {
            ServerPropertyResolver?.Invoke($"_WRITEFILE={resolvedArg}");
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
        if (key.HasArg && target.TrySetProperty(cmd, resolvedArg))
        {
            if (_expr.DebugUnresolved)
                _logger.LogDebug("[script_exec] handled via setprop '{Cmd}'", cmd);
            return;
        }

        // Client-bound dialog verbs should still work when script fallback
        // runs without a concrete ITextConsole (source can be null in some
        // call paths). Route directly to the target object's owning client
        // through the same _REF_EXEC bridge used by UID.<...>.DIALOG.
        if (cmd.Equals("DIALOG", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("SDIALOG", StringComparison.OrdinalIgnoreCase))
        {
            if (target.TryGetProperty("UID", out string dialogUid) && !string.IsNullOrWhiteSpace(dialogUid))
            {
                ServerPropertyResolver?.Invoke($"_REF_EXEC={dialogUid}|DIALOG|{resolvedArg}");
                if (_expr.DebugUnresolved)
                    _logger.LogDebug("[script_exec] handled via dialog bridge uid='{Uid}' arg='{Arg}'", dialogUid, resolvedArg);
                return;
            }
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
            // Pass resolvedArg as the new <ARGS> for the called function
            var funcArgs = new TriggerArgs
            {
                Source = args?.Source,
                Object1 = args?.Object1,
                Object2 = args?.Object2,
                Number1 = args?.Number1 ?? 0,
                Number2 = args?.Number2 ?? 0,
                Number3 = args?.Number3 ?? 0,
                ArgString = resolvedArg
            };
            InvokeFunction(cmd, target, source, funcArgs, scope);
            if (_expr.DebugUnresolved)
                _logger.LogDebug("[script_exec] delegated to function '{Cmd}'", cmd);
            return;
        }

        _logger.LogWarning("Unhandled script line: {Key}={Arg}", cmd, resolvedArg);
    }

    private int ExecuteIf(IReadOnlyList<ScriptKey> lines, int startIdx, IScriptObj target,
        ITextConsole? source, ITriggerArgs? args, ScriptScope scope, out TriggerResult result)
    {
        result = TriggerResult.Default;
        int i = startIdx;

        string condition = ResolveArgs(lines[i].Arg, target, source, args, scope);
        bool condResult = EvaluateWithResolver(condition, target, source, args, scope) != 0;
        bool branchTaken = condResult;
        i++;

        while (i < lines.Count)
        {
            string cmd = lines[i].Key.ToUpperInvariant();

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
                    condResult = EvaluateWithResolver(elifCond, target, source, args, scope) != 0;
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
                switch (cmd)
                {
                    case "IF":
                        i = ExecuteIf(lines, i, target, source, args, scope, out result);
                        break;
                    case "FOR":
                        i = ExecuteFor(lines, i, target, source, args, scope, out result);
                        break;
                    case "WHILE":
                        i = ExecuteWhile(lines, i, target, source, args, scope, out result);
                        break;
                    case "RETURN":
                    {
                        string argStr = ResolveArgs(lines[i].Arg, target, source, args, scope);
                        bool numeric = TryEvaluateWithResolver(argStr, target, source, args, scope, out long val);
                        scope.ReturnValue = numeric ? val.ToString() : argStr;
                        scope.IsReturning = true;
                        return lines.Count;
                    }
                    default:
                        ExecuteLine(lines[i], target, source, args, scope);
                        i++;
                        break;
                }

                if (scope.IsReturning) return lines.Count;
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
        bool countDown = min > max;
        long iterations = 0;
        for (long v = min;
             (countDown ? v >= max : v <= max) && iterations < scope.MaxLoopIterations;
             v += countDown ? -1 : 1, iterations++)
        {
            scope.LocalVars.SetInt("_FOR", v);
            if (!loopVar.Equals("_FOR", StringComparison.OrdinalIgnoreCase))
                scope.LocalVars.SetInt(loopVar, v);
            result = Execute(GetSubList(lines, bodyStart, bodyEnd), target, source, args, scope);
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
        int iterations = 0;
        while (iterations < scope.MaxLoopIterations)
        {
            string resolved = ResolveArgs(condition, target, source, args, scope);
            if (EvaluateWithResolver(resolved, target, source, args, scope) == 0)
                break;

            result = Execute(GetSubList(lines, bodyStart, bodyEnd), target, source, args, scope);
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
            ExecuteLine(lines[options[pick]], target, source, args, scope);
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

        // Source-X behavior: each loop iteration changes the default object (target)
        // to the iterated object, while also setting ARGO for compatibility.
        IScriptObj? prevObj1 = args?.Object1;
        int iterations = 0;
        scope.LoopDepth++;
        foreach (var obj in objects)
        {
            if (iterations++ >= scope.MaxLoopIterations) break;
            if (args is TriggerArgs ta)
                ta.Object1 = obj;
            result = Execute(body, obj, source, args, scope);
            if (scope.IsContinuing) { scope.IsContinuing = false; continue; }
            if (scope.IsBreaking) { scope.IsBreaking = false; break; }
            if (scope.IsReturning) break;
        }
        scope.LoopDepth--;
        if (args is TriggerArgs ta2)
            ta2.Object1 = prevObj1;

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

    private string ResolveArgs(string arg, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope = null)
    {
        if (string.IsNullOrEmpty(arg)) return "";
        if (arg.IndexOf('<') < 0) return arg;

        // Set resolver to target object for <property> lookups
        var oldResolver = _expr.VariableResolver;
        var oldFunctionResolver = _expr.FunctionResolver;
        try
        {
            _expr.VariableResolver = varName => ResolveVarForTarget(varName, target, source, args, scope);
            _expr.FunctionResolver = expr => TryResolveFunctionExpression(expr, target, source, args, scope);
            return _expr.EvaluateStr(arg);
        }
        finally
        {
            _expr.VariableResolver = oldResolver;
            _expr.FunctionResolver = oldFunctionResolver;
        }
    }

    private long EvaluateWithResolver(string expr, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope = null)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return 0;
        var oldResolver = _expr.VariableResolver;
        var oldFunctionResolver = _expr.FunctionResolver;
        _expr.VariableResolver = varName => ResolveVarForTarget(varName, target, source, args, scope);
        _expr.FunctionResolver = exprText => TryResolveFunctionExpression(exprText, target, source, args, scope);
        try
        {
            return _expr.Evaluate(expr.AsSpan());
        }
        finally
        {
            _expr.VariableResolver = oldResolver;
            _expr.FunctionResolver = oldFunctionResolver;
        }
    }

    /// <summary>Evaluate with the target-bound resolvers, reporting whether the string
    /// was a genuine numeric expression (vs a string literal — e.g. a RETURN of a name
    /// or defname). Lets RETURN preserve string values instead of collapsing to 0.</summary>
    private bool TryEvaluateWithResolver(string expr, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope, out long value)
    {
        if (string.IsNullOrWhiteSpace(expr)) { value = 0; return false; }
        var oldResolver = _expr.VariableResolver;
        var oldFunctionResolver = _expr.FunctionResolver;
        _expr.VariableResolver = varName => ResolveVarForTarget(varName, target, source, args, scope);
        _expr.FunctionResolver = exprText => TryResolveFunctionExpression(exprText, target, source, args, scope);
        try
        {
            return _expr.TryEvaluate(expr.AsSpan(), out value);
        }
        finally
        {
            _expr.VariableResolver = oldResolver;
            _expr.FunctionResolver = oldFunctionResolver;
        }
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

    /// <summary>Parse an ARGN assignment value: decimal, or hex with a 0x prefix.</summary>
    private static bool TryParseArgN(string s, out int value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        return int.TryParse(s, out value);
    }

    private string? ResolveVarForTarget(string varName, IScriptObj target, ITextConsole? source, ITriggerArgs? args, ScriptScope? scope = null)
    {
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
        if (varName.Equals("ARGN1", StringComparison.OrdinalIgnoreCase) ||
            varName.Equals("ARGN", StringComparison.OrdinalIgnoreCase))
            return args?.Number1.ToString() ?? "0";
        if (varName.Equals("ARGN2", StringComparison.OrdinalIgnoreCase))
            return args?.Number2.ToString() ?? "0";
        if (varName.Equals("ARGN3", StringComparison.OrdinalIgnoreCase))
            return args?.Number3.ToString() ?? "0";
        if (varName.Equals("ARGO", StringComparison.OrdinalIgnoreCase))
            return GetObjectRef(args?.Object1);
        if (varName.StartsWith("ARGO.", StringComparison.OrdinalIgnoreCase))
        {
            string subProp = varName[5..];
            if (args?.Object1 != null && args.Object1.TryGetProperty(subProp, out string objVal))
                return objVal;
            return "0";
        }
        if (varName.Equals("ACT", StringComparison.OrdinalIgnoreCase))
            return GetObjectRef(args?.Object2);
        if (varName.StartsWith("ACT.", StringComparison.OrdinalIgnoreCase))
        {
            string subProp = varName[4..];
            if (args?.Object2 != null && args.Object2.TryGetProperty(subProp, out string objVal))
                return objVal;
            return "0";
        }
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
            if (args == null || string.IsNullOrEmpty(args.ArgString))
                return "";

            IReadOnlyList<string> argv = args is TriggerArgs triggerArgs
                ? triggerArgs.GetArgv()
                : args.ArgString.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int idx = 0;
            string suffix = varName.Length > 4 ? varName[4..] : "";
            if (suffix.StartsWith("[", StringComparison.Ordinal) && suffix.EndsWith("]", StringComparison.Ordinal) && suffix.Length > 2)
                suffix = suffix[1..^1];
            if (int.TryParse(suffix, out int parsed))
                idx = parsed;
            return (idx >= 0 && idx < argv.Count) ? argv[idx] : "";
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
            if (args?.Source != null && args.Source.TryGetProperty(subProp, out string srcVal))
                return srcVal;
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

        // SERV.* — server-level property resolution
        if (varName.StartsWith("SERV.", StringComparison.OrdinalIgnoreCase))
        {
            string servProp = varName[5..];
            string? servVal = ServerPropertyResolver?.Invoke(servProp);
            if (servVal != null) return servVal;
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
            string cmd = lines[i].Key.ToUpperInvariant();

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
        switch (cmd)
        {
            case "IF":
            {
                int depth = 1;
                idx++;
                while (idx < lines.Count && depth > 0)
                {
                    string c = lines[idx].Key.ToUpperInvariant();
                    if (c == "IF") depth++;
                    if (c == "ENDIF") depth--;
                    if (depth > 0) idx++;
                }
                return idx + 1;
            }
            case "FOR":
            case "FORPLAYERS":
            case "FORCHARS":
            case "FORITEMS":
            case "FORCLIENTS":
            case "FOROBJS":
            case "FORINSTANCES":
            case "FORCONT":
            case "FORCONTID":
            case "FORCONTTYPE":
            {
                int depth = 1;
                idx++;
                while (idx < lines.Count && depth > 0)
                {
                    string c = lines[idx].Key.ToUpperInvariant();
                    if (c == "FOR" || c.StartsWith("FOR", StringComparison.Ordinal) && IsForVariant(c))
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
                    string c = lines[idx].Key.ToUpperInvariant();
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
                    string c = lines[idx].Key.ToUpperInvariant();
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
                    string c = lines[idx].Key.ToUpperInvariant();
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

    private static bool IsForVariant(string cmd) =>
        cmd is "FORPLAYERS" or "FORCHARS" or "FORITEMS" or "FORCLIENTS"
            or "FOROBJS" or "FORINSTANCES" or "FORCONT" or "FORCONTID" or "FORCONTTYPE";

    private static IReadOnlyList<ScriptKey> GetSubList(IReadOnlyList<ScriptKey> lines, int start, int end)
    {
        if (start >= end || start >= lines.Count) return [];
        int count = Math.Min(end, lines.Count) - start;
        var result = new ScriptKey[count];
        for (int i = 0; i < count; i++)
            result[i] = lines[start + i];
        return result;
    }

    private sealed class NullConsole : ITextConsole
    {
        public static readonly NullConsole Instance = new();
        public PrivLevel GetPrivLevel() => PrivLevel.Guest;
        public void SysMessage(string text) { }
        public string GetName() => "SYSTEM";
    }

    /// <summary>Server source context for TRYSRV (Source-X g_Serv): maximum
    /// privilege so a TRYSRV verb bypasses the original source's PLEVEL checks.</summary>
    private sealed class ServerConsole : ITextConsole
    {
        public static readonly ServerConsole Instance = new();
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "SERV";
    }
}
