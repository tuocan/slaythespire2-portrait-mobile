using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length < 2 || args.Length > 3)
{
    Console.Error.WriteLine("Usage: SteamKitPatch <SteamKit2.dll> <STS2Mobile.dll> [sts2.dll]");
    Environment.Exit(2);
}

var steamKitPath = args[0];
var helperPath = args[1];
var gamePath = args.Length == 3 ? args[2] : null;
var resolver = CreateResolver(steamKitPath, helperPath, gamePath);
var reader = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
var steamKit = ModuleDefinition.ReadModule(steamKitPath, reader);
var helper = ModuleDefinition.ReadModule(helperPath, reader);

var helperType = helper.GetType("STS2Mobile.Steam.AndroidJavaCrypto")
    ?? throw new InvalidOperationException("Could not find AndroidJavaCrypto helper type");
if (!helperType.IsPublic)
    throw new InvalidOperationException("AndroidJavaCrypto must be public because SteamKit2 calls it across an assembly boundary");

var cryptoPatchRules = CreateCryptoPatchRules(helperType, steamKit);
var cryptoPatchCounts = PatchCryptoCalls(steamKit, cryptoPatchRules);
AssertRequiredCryptoPatches(cryptoPatchCounts);
AssertNoForbiddenCryptoCallsites(steamKit);

WriteModuleOverOriginal(steamKit, steamKitPath);
Console.WriteLine(FormatCryptoPatchSummary(cryptoPatchCounts));

if (!string.IsNullOrWhiteSpace(gamePath))
    PatchGamePlatformUtil(gamePath, resolver, helper);
helper.Dispose();

static DefaultAssemblyResolver CreateResolver(params string?[] assemblyPaths)
{
    var resolver = new DefaultAssemblyResolver();
    foreach (var assemblyPath in assemblyPaths)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            continue;

        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
    }

    // Reference-only directory (the game's own managed assemblies). Rewriting sts2.dll
    // needs Sentry.dll to resolve constant types, but Sentry.dll is deliberately not
    // shipped in dotnet_bcl, so it is resolved from here without being packaged.
    var referenceDir = Environment.GetEnvironmentVariable("STS2_PATCH_REFERENCE_DIR");
    if (!string.IsNullOrWhiteSpace(referenceDir) && Directory.Exists(referenceDir))
        resolver.AddSearchDirectory(Path.GetFullPath(referenceDir));

    return resolver;
}

static IReadOnlyList<CryptoPatchRule> CreateCryptoPatchRules(TypeDefinition helperType, ModuleDefinition targetModule)
{
    var ruleSpecs = new CryptoPatchRuleSpec[]
    {
        new(
            "rng",
            "System.Security.Cryptography.RandomNumberGenerator",
            new("GetBytes", "System.Byte[]", "System.Int32"),
            new("GetRandomBytes", "System.Byte[]", "System.Int32")
        ),
        new(
            "fill",
            "System.Security.Cryptography.RandomNumberGenerator",
            new("Fill", "System.Void", "System.Span`1<System.Byte>"),
            new("FillRandom", "System.Void", "System.Span`1<System.Byte>")
        ),
        new(
            "spki",
            "System.Security.Cryptography.AsymmetricAlgorithm",
            new("ImportSubjectPublicKeyInfo", "System.Void", "System.ReadOnlySpan`1<System.Byte>", "System.Int32&"),
            new("ImportSubjectPublicKeyInfo", "System.Void", "System.Security.Cryptography.AsymmetricAlgorithm", "System.ReadOnlySpan`1<System.Byte>", "System.Int32&")
        ),
        new(
            "parameters",
            "System.Security.Cryptography.RSA",
            new("ImportParameters", "System.Void", "System.Security.Cryptography.RSAParameters"),
            new("ImportParameters", "System.Void", "System.Security.Cryptography.RSA", "System.Security.Cryptography.RSAParameters")
        ),
        new(
            "rsaEncrypt",
            "System.Security.Cryptography.RSA",
            new("Encrypt", "System.Byte[]", "System.Byte[]", "System.Security.Cryptography.RSAEncryptionPadding"),
            new("RsaEncrypt", "System.Byte[]", "System.Security.Cryptography.RSA", "System.Byte[]", "System.Security.Cryptography.RSAEncryptionPadding")
        ),
        new(
            "createRsa",
            "System.Security.Cryptography.RSA",
            new("Create", "System.Security.Cryptography.RSA"),
            new("CreateRsa", "System.Security.Cryptography.RSA")
        ),
        new(
            "hmacSha1Bytes",
            "System.Security.Cryptography.HMACSHA1",
            new("HashData", "System.Byte[]", "System.Byte[]", "System.Byte[]"),
            new("HmacSha1HashData", "System.Byte[]", "System.Byte[]", "System.Byte[]")
        ),
        new(
            "hmacSha1Span",
            "System.Security.Cryptography.HMACSHA1",
            new("HashData", "System.Byte[]", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>"),
            new("HmacSha1HashData", "System.Byte[]", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>")
        ),
        new(
            "hmacSha1SpanDestination",
            "System.Security.Cryptography.HMACSHA1",
            new("HashData", "System.Int32", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>"),
            new("HmacSha1HashData", "System.Int32", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>")
        ),
        new(
            "sha1Bytes",
            "System.Security.Cryptography.SHA1",
            new("HashData", "System.Byte[]", "System.Byte[]"),
            new("Sha1HashData", "System.Byte[]", "System.Byte[]")
        ),
        new(
            "createAes",
            "System.Security.Cryptography.Aes",
            new("Create", "System.Security.Cryptography.Aes"),
            new("CreateAes", "System.Security.Cryptography.Aes")
        ),
        new(
            "aesEncryptEcb",
            null,
            new("EncryptEcb", "System.Int32", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Security.Cryptography.PaddingMode"),
            new("AesEncryptEcb", "System.Int32", "System.Security.Cryptography.SymmetricAlgorithm", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Security.Cryptography.PaddingMode")
        ),
        new(
            "aesEncryptCbc",
            null,
            new("EncryptCbc", "System.Int32", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Security.Cryptography.PaddingMode"),
            new("AesEncryptCbc", "System.Int32", "System.Security.Cryptography.SymmetricAlgorithm", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Security.Cryptography.PaddingMode")
        ),
        new(
            "aesDecryptEcb",
            null,
            new("DecryptEcb", "System.Int32", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Security.Cryptography.PaddingMode"),
            new("AesDecryptEcb", "System.Int32", "System.Security.Cryptography.SymmetricAlgorithm", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Security.Cryptography.PaddingMode")
        ),
        new(
            "aesDecryptCbcSpanDestination",
            null,
            new("DecryptCbc", "System.Int32", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Security.Cryptography.PaddingMode"),
            new("AesDecryptCbc", "System.Int32", "System.Security.Cryptography.SymmetricAlgorithm", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>", "System.Span`1<System.Byte>", "System.Security.Cryptography.PaddingMode")
        ),
        new(
            "aesDecryptCbc",
            null,
            new("DecryptCbc", "System.Byte[]", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>", "System.Security.Cryptography.PaddingMode"),
            new("AesDecryptCbc", "System.Byte[]", "System.Security.Cryptography.SymmetricAlgorithm", "System.ReadOnlySpan`1<System.Byte>", "System.ReadOnlySpan`1<System.Byte>", "System.Security.Cryptography.PaddingMode")
        ),
    };

    return ruleSpecs
        .Select(spec => new CryptoPatchRule(
            spec.CountName,
            spec.DeclaringType,
            spec.Target,
            targetModule.ImportReference(FindHelper(helperType, spec.Helper))
        ))
        .ToArray();
}

static Dictionary<string, int> PatchCryptoCalls(ModuleDefinition module, IReadOnlyList<CryptoPatchRule> rules)
{
    var counts = rules.ToDictionary(rule => rule.CountName, _ => 0);

    foreach (var type in module.Types.SelectMany(WalkTypes))
    {
        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
                continue;

            foreach (var instruction in method.Body.Instructions)
            {
                if (!IsCallInstruction(instruction) || instruction.Operand is not MethodReference methodReference)
                    continue;

                var matchingRule = rules.FirstOrDefault(rule => rule.Matches(methodReference));
                if (matchingRule is null)
                    continue;

                instruction.OpCode = OpCodes.Call;
                instruction.Operand = matchingRule.ImportedHelper;
                counts[matchingRule.CountName]++;
            }
        }
    }

    return counts;
}

static void AssertRequiredCryptoPatches(IReadOnlyDictionary<string, int> counts)
{
    RequirePatch(counts, "rng", "No RandomNumberGenerator.GetBytes(int) calls were patched");
    RequirePatch(counts, "fill", "No RandomNumberGenerator.Fill(Span<byte>) calls were patched");
    RequirePatch(counts, "spki", "No AsymmetricAlgorithm.ImportSubjectPublicKeyInfo calls were patched");
    RequirePatch(counts, "parameters", "No RSA.ImportParameters calls were patched");
    RequirePatch(counts, "rsaEncrypt", "No RSA.Encrypt calls were patched");
    RequirePatch(counts, "createRsa", "No RSA.Create() calls were patched");
    RequirePatch(counts, "sha1Bytes", "No SHA1.HashData(byte[]) calls were patched");
    RequirePatch(counts, "createAes", "No Aes.Create() calls were patched");
    RequirePatch(counts, "aesEncryptEcb", "No AES EncryptEcb calls were patched");
    RequirePatch(counts, "aesEncryptCbc", "No AES EncryptCbc calls were patched");
    RequirePatch(counts, "aesDecryptEcb", "No AES DecryptEcb calls were patched");

    if (Sum(counts, "hmacSha1Bytes", "hmacSha1Span", "hmacSha1SpanDestination") == 0)
        throw new InvalidOperationException("No HMACSHA1.HashData calls were patched");
    if (Sum(counts, "aesDecryptCbc", "aesDecryptCbcSpanDestination") == 0)
        throw new InvalidOperationException("No AES DecryptCbc calls were patched");
}

static string FormatCryptoPatchSummary(IReadOnlyDictionary<string, int> counts)
{
    var orderedNames = new[]
    {
        "rng",
        "fill",
        "spki",
        "parameters",
        "rsaEncrypt",
        "createRsa",
        "hmacSha1Bytes",
        "hmacSha1Span",
        "hmacSha1SpanDestination",
        "sha1Bytes",
        "createAes",
        "aesEncryptEcb",
        "aesEncryptCbc",
        "aesDecryptEcb",
        "aesDecryptCbcSpanDestination",
        "aesDecryptCbc"
    };

    return "Patched SteamKit2.dll: " + string.Join(", ", orderedNames.Select(name => $"{name}={counts[name]}"));
}

static MethodDefinition FindHelper(TypeDefinition helperType, MethodSignature signature)
{
    var method = helperType.Methods.FirstOrDefault(signature.Matches)
        ?? throw new InvalidOperationException($"Could not find AndroidJavaCrypto.{signature.Name}");
    if (!method.IsPublic)
        throw new InvalidOperationException(
            $"AndroidJavaCrypto.{signature.Name} must be public because SteamKit2 calls it across an assembly boundary"
        );

    return method;
}

static void RequirePatch(IReadOnlyDictionary<string, int> counts, string name, string message)
{
    if (counts[name] == 0)
        throw new InvalidOperationException(message);
}

static int Sum(IReadOnlyDictionary<string, int> counts, params string[] names)
{
    return names.Sum(name => counts[name]);
}

static bool IsCallInstruction(Instruction instruction)
{
    return instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt;
}

static void WriteModuleOverOriginal(ModuleDefinition module, string path)
{
    var tmpPath = path + ".patched";
    module.Write(tmpPath);
    module.Dispose();
    File.Copy(tmpPath, path, true);
    File.Delete(tmpPath);
}

static IEnumerable<TypeDefinition> WalkTypes(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes.SelectMany(WalkTypes))
        yield return nested;
}

static void AssertNoForbiddenCryptoCallsites(ModuleDefinition module)
{
    var remaining = new Dictionary<string, int>();

    foreach (var type in module.Types.SelectMany(WalkTypes))
    {
        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
                continue;

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference mr)
                    continue;

                if (!IsForbiddenCryptoCallsite(mr))
                    continue;

                var signature =
                    $"{mr.DeclaringType.FullName}::{mr.Name}(" +
                    string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName)) +
                    $")->{mr.ReturnType.FullName}";
                remaining.TryGetValue(signature, out var count);
                remaining[signature] = count + 1;
            }
        }
    }

    if (remaining.Count > 0)
    {
        throw new InvalidOperationException(
            "Forbidden Android SteamKit crypto callsites remain after patching: " +
            string.Join("; ", remaining.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Value}x {kvp.Key}"))
        );
    }
}

static bool IsForbiddenCryptoCallsite(MethodReference mr)
{
    var typeName = mr.DeclaringType.FullName;
    var methodName = mr.Name;

    if (typeName == "System.Security.Cryptography.RandomNumberGenerator" &&
        (methodName == "GetBytes" || methodName == "Fill"))
    {
        return true;
    }

    if (typeName == "System.Security.Cryptography.AsymmetricAlgorithm" &&
        methodName == "ImportSubjectPublicKeyInfo")
    {
        return true;
    }

    if (typeName == "System.Security.Cryptography.RSA" &&
        (methodName == "Create" || methodName == "ImportParameters" || methodName == "Encrypt"))
    {
        return true;
    }

    if ((typeName == "System.Security.Cryptography.HMACSHA1" ||
         typeName == "System.Security.Cryptography.SHA1") &&
        methodName == "HashData")
    {
        return true;
    }

    if (typeName == "System.Security.Cryptography.Aes" && methodName == "Create")
    {
        return true;
    }

    if (typeName.StartsWith("System.Security.Cryptography.", StringComparison.Ordinal) &&
        (methodName == "EncryptEcb" ||
         methodName == "EncryptCbc" ||
         methodName == "DecryptEcb" ||
         methodName == "DecryptCbc"))
    {
        return true;
    }

    return false;
}

// Game v0.111 changed ModelDb.Init to Init(Type[] injectedModelTypes). Building a
// Harmony replacement for that method aborts Mono on Android natively
// ("mono_class_from_mono_type_internal: implement me 0x00", class.c:2324), so the
// two-phase registry init is wired in here instead:
//   ModelDb.Init:     if (STS2Mobile.Patches.ModelDbHooks.Init(arg)) return; <original>
//   ModelDb.Contains: if (__sts2mobile_SuppressContains) return false; <original>
// STS2Mobile detects the added field and skips its runtime Harmony patch.
static void PatchModelDbInit(ModuleDefinition game, ModuleDefinition helper)
{
    var modelDb = game.GetType("MegaCrit.Sts2.Core.Models.ModelDb");
    var init = modelDb?.Methods.FirstOrDefault(m =>
        m.Name == "Init" && m.IsStatic && m.HasBody &&
        m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Type[]");
    if (modelDb == null || init == null)
    {
        Console.WriteLine("sts2.dll has no ModelDb.Init(Type[]); ModelDb stays on the runtime Harmony patch");
        return;
    }

    var hookType = helper.GetType("STS2Mobile.Patches.ModelDbHooks")
        ?? throw new InvalidOperationException("Could not find STS2Mobile.Patches.ModelDbHooks");
    if (!hookType.IsPublic)
        throw new InvalidOperationException("ModelDbHooks must be public because sts2.dll calls it across an assembly boundary");
    var hookInit = hookType.Methods.FirstOrDefault(m =>
        m.Name == "Init" && m.IsStatic && m.IsPublic && m.ReturnType.FullName == "System.Boolean" &&
        m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Type[]")
        ?? throw new InvalidOperationException("Could not find public static bool ModelDbHooks.Init(Type[])");
    var hookInitRef = game.ImportReference(hookInit);

    const string flagName = "__sts2mobile_SuppressContains";
    if (modelDb.Fields.Any(f => f.Name == flagName))
        throw new InvalidOperationException("sts2.dll is already patched (ModelDb flag present); patch a fresh copy");
    var flag = new FieldDefinition(flagName, FieldAttributes.Public | FieldAttributes.Static, game.TypeSystem.Boolean);
    modelDb.Fields.Add(flag);

    PrependGuard(init, (il, original) => new[]
    {
        il.Create(OpCodes.Ldarg_0),
        il.Create(OpCodes.Call, hookInitRef),
        il.Create(OpCodes.Brfalse, original),
        il.Create(OpCodes.Ret),
    });

    var contains = modelDb.Methods.FirstOrDefault(m =>
        m.Name == "Contains" && m.IsStatic && m.HasBody && m.ReturnType.FullName == "System.Boolean" &&
        m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Type");
    if (contains != null)
    {
        PrependGuard(contains, (il, original) => new[]
        {
            il.Create(OpCodes.Ldsfld, flag),
            il.Create(OpCodes.Brfalse, original),
            il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Ret),
        });
    }

    // v0.111's AbstractModel constructor no longer asks ModelDb.Contains; it throws
    // DuplicateModelException when ModelDb.GetByIdOrNull<AbstractModel>(id) finds an
    // entry. Phase 1 pre-registers every model, so Phase 2 must see "no entry" there:
    //   var existing = GetByIdOrNull<...>(id); if (flag) existing = null;
    var abstractModel = game.GetType("MegaCrit.Sts2.Core.Models.AbstractModel");
    var modelCtor = abstractModel?.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0 && m.HasBody);
    var duplicateLookup = modelCtor?.Body.Instructions.FirstOrDefault(i =>
        i.OpCode == OpCodes.Call &&
        i.Operand is GenericInstanceMethod g &&
        g.ElementMethod.Name == "GetByIdOrNull" &&
        g.ElementMethod.DeclaringType.FullName == modelDb.FullName);
    if (modelCtor != null && duplicateLookup != null)
    {
        modelCtor.Body.SimplifyMacros();
        var il = modelCtor.Body.GetILProcessor();
        var next = duplicateLookup.Next;
        il.InsertBefore(next, il.Create(OpCodes.Ldsfld, flag));
        il.InsertBefore(next, il.Create(OpCodes.Brfalse, next));
        il.InsertBefore(next, il.Create(OpCodes.Pop));
        il.InsertBefore(next, il.Create(OpCodes.Ldnull));
        modelCtor.Body.OptimizeMacros();
    }

    Console.WriteLine(
        $"Patched sts2.dll ModelDb.Init(Type[]): build-time two-phase hook; Contains guard={(contains != null)}; " +
        $"AbstractModel duplicate-check guard={(duplicateLookup != null)}");
}

static void PrependGuard(MethodDefinition method, Func<ILProcessor, Instruction, Instruction[]> guard)
{
    method.Body.SimplifyMacros();
    var il = method.Body.GetILProcessor();
    var original = method.Body.Instructions[0];
    foreach (var instruction in guard(il, original))
        il.InsertBefore(original, instruction);
    method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 2);
    method.Body.OptimizeMacros();
}

static void PatchGamePlatformUtil(string gamePath, IAssemblyResolver resolver, ModuleDefinition helper)
{
    var reader = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
    var game = ModuleDefinition.ReadModule(gamePath, reader);
    var platformUtil = game.GetType("MegaCrit.Sts2.Core.Platform.PlatformUtil")
        ?? throw new InvalidOperationException("Could not find PlatformUtil in sts2.dll");
    var nullStrategy = game.Types
        .SelectMany(WalkTypes)
        .FirstOrDefault(t => t.Name == "NullPlatformUtilStrategy")
        ?? throw new InvalidOperationException("Could not find NullPlatformUtilStrategy in sts2.dll");
    var nullStrategyCtor = nullStrategy.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0)
        ?? throw new InvalidOperationException("Could not find NullPlatformUtilStrategy constructor in sts2.dll");
    var objectCtor = game.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
    var nullField = platformUtil.Fields.FirstOrDefault(f => f.Name == "_null")
        ?? throw new InvalidOperationException("Could not find PlatformUtil._null field in sts2.dll");
    var steamField = platformUtil.Fields.FirstOrDefault(f => f.Name == "_steam");

    ReplaceBody(nullStrategyCtor, il =>
    {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, objectCtor));
        il.Append(il.Create(OpCodes.Ret));
    });

    var staticCtor = platformUtil.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic)
        ?? throw new InvalidOperationException("Could not find PlatformUtil static constructor in sts2.dll");
    ReplaceBody(staticCtor, il =>
    {
        il.Append(il.Create(OpCodes.Newobj, nullStrategyCtor));
        il.Append(il.Create(OpCodes.Stsfld, nullField));
        if (steamField != null)
        {
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Stsfld, steamField));
        }
        il.Append(il.Create(OpCodes.Ret));
    });

    var primaryPlatform = platformUtil.Methods.FirstOrDefault(m => m.Name == "get_PrimaryPlatform" && m.Parameters.Count == 0)
        ?? throw new InvalidOperationException("Could not find PlatformUtil.PrimaryPlatform getter in sts2.dll");
    ReplaceBody(primaryPlatform, il =>
    {
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ret));
    });

    var getPlatformUtil = platformUtil.Methods.FirstOrDefault(m => m.Name == "GetPlatformUtil" && m.Parameters.Count == 1)
        ?? throw new InvalidOperationException("Could not find PlatformUtil.GetPlatformUtil in sts2.dll");
    ReplaceBody(getPlatformUtil, il =>
    {
        il.Append(il.Create(OpCodes.Ldsfld, nullField));
        il.Append(il.Create(OpCodes.Ret));
    });

    // Newer game builds (v0.111+) ship a module initializer (<Module>..cctor) that calls
    // SentryAutoInit.Init -> Sentry.Godot.Internal.SentryGodotInitializer.AutoInit.
    // Sentry.Godot.dll is not packaged for Android, so the module initializer throws and
    // Mono reports a TypeInitializationException while creating the GodotPlugins delegate
    // (gray screen, ".NET: Failed to get GodotPlugins initialization function pointer").
    // Sentry is disabled on Android anyway, so make the auto-init a no-op.
    var sentryAutoInit = game.Types
        .SelectMany(WalkTypes)
        .FirstOrDefault(t => t.Name == "SentryAutoInit");
    var sentryInit = sentryAutoInit?.Methods.FirstOrDefault(m => m.Name == "Init" && m.IsStatic && m.Parameters.Count == 0);
    if (sentryInit != null)
    {
        ReplaceBody(sentryInit, il => il.Append(il.Create(OpCodes.Ret)));
        Console.WriteLine("Patched sts2.dll SentryAutoInit.Init: no-op (module initializer no longer touches Sentry.Godot)");
    }
    else
    {
        Console.WriteLine("sts2.dll has no SentryAutoInit.Init; module initializer patch not needed");
    }

    PatchModelDbInit(game, helper);

    WriteModuleOverOriginal(game, gamePath);
    Console.WriteLine("Patched sts2.dll PlatformUtil: NullPlatformUtilStrategy constructor no-op, static constructor uses NullPlatformUtilStrategy, PrimaryPlatform=None, GetPlatformUtil returns _null");
}

static void ReplaceBody(MethodDefinition method, Action<ILProcessor> emit)
{
    if (!method.HasBody)
        method.Body = new MethodBody(method);

    method.Body.ExceptionHandlers.Clear();
    method.Body.Variables.Clear();
    method.Body.Instructions.Clear();
    method.Body.InitLocals = false;
    method.Body.MaxStackSize = 8;
    emit(method.Body.GetILProcessor());
}

sealed record MethodSignature(string Name, string ReturnType, params string[] ParameterTypes)
{
    public bool Matches(MethodReference method)
    {
        return method.Name == Name &&
            method.ReturnType.FullName == ReturnType &&
            method.Parameters.Count == ParameterTypes.Length &&
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(ParameterTypes);
    }
}

sealed record CryptoPatchRuleSpec(
    string CountName,
    string? DeclaringType,
    MethodSignature Target,
    MethodSignature Helper
);

sealed record CryptoPatchRule(
    string CountName,
    string? DeclaringType,
    MethodSignature Target,
    MethodReference ImportedHelper
)
{
    public bool Matches(MethodReference method)
    {
        return (DeclaringType is null || method.DeclaringType.FullName == DeclaringType) && Target.Matches(method);
    }
}
