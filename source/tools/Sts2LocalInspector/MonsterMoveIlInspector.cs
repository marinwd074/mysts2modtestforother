using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Sts2LocalInspector;

internal static class MonsterMoveIlInspector
{
    internal const string Pinned01071Sha256 =
        "a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52";

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .GroupBy(opcode => unchecked((ushort)opcode.Value))
            .ToDictionary(group => group.Key, group => group.First());

    internal static MonsterMoveIlEvidence InspectPinned01071(string assemblyPath)
    {
        string actualHash;
        using (FileStream hashStream = File.OpenRead(assemblyPath))
            actualHash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();
        if (!string.Equals(actualHash, Pinned01071Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "monster move audit requires pinned STS2 0.107.1 sts2.dll; expected sha256="
                + Pinned01071Sha256 + " actual=" + actualHash);
        }

        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
            throw new InvalidDataException(assemblyPath + " has no .NET metadata.");

        MetadataReader reader = pe.GetMetadataReader();
        List<MonsterMoveMethodIl> methods = [];
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            if (!TryGetMonsterOwner(reader, typeHandle, out string? monsterType))
                continue;

            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            string declaringTypeName = reader.GetString(type.Name);
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                string methodName = reader.GetString(method.Name);
                bool moveMethod = methodName.Contains("Move", StringComparison.Ordinal);
                // Native move handlers are not consistently named *Move (for example
                // ShockingSlap and ThunderStrike). Their async state machines still expose
                // MoveNext, so retain every compiler-generated async body nested under a
                // monster type and let the audit filter by declaring handler name.
                bool asyncMoveBody = string.Equals(methodName, "MoveNext", StringComparison.Ordinal)
                    && declaringTypeName.StartsWith("<", StringComparison.Ordinal)
                    && declaringTypeName.Contains(">d__", StringComparison.Ordinal);
                if ((!moveMethod && !asyncMoveBody) || method.RelativeVirtualAddress == 0)
                    continue;

                MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
                methods.Add(new MonsterMoveMethodIl(
                    monsterType!,
                    declaringTypeName,
                    methodName,
                    MetadataTokens.GetToken(methodHandle),
                    Decode(body.GetILBytes() ?? [], reader)));
            }
        }

        methods.Sort((left, right) =>
        {
            int result = StringComparer.Ordinal.Compare(left.MonsterType, right.MonsterType);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.DeclaringType, right.DeclaringType);
            return result != 0
                ? result
                : StringComparer.Ordinal.Compare(left.Method, right.Method);
        });
        return new MonsterMoveIlEvidence(1, actualHash, methods);
    }

    internal static int SelfTestDecoder(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
            throw new InvalidDataException(assemblyPath + " has no .NET metadata.");

        MetadataReader reader = pe.GetMetadataReader();
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            if (!string.Equals(reader.GetString(type.Namespace), "Sts2LocalInspector", StringComparison.Ordinal)
                || !string.Equals(reader.GetString(type.Name), "Program", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if (!string.Equals(reader.GetString(method.Name), "Main", StringComparison.Ordinal)
                    || method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
                IReadOnlyList<IlInstructionEvidence> decoded =
                    Decode(body.GetILBytes() ?? [], reader);
                return decoded.Count;
            }
        }

        throw new InvalidDataException("Sts2LocalInspector.Program.Main IL body was not found.");
    }

    private static bool TryGetMonsterOwner(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        out string? monsterType)
    {
        TypeDefinitionHandle current = handle;
        while (true)
        {
            TypeDefinition type = reader.GetTypeDefinition(current);
            TypeDefinitionHandle parent = type.GetDeclaringType();
            if (!parent.IsNil)
            {
                current = parent;
                continue;
            }

            string ns = reader.GetString(type.Namespace);
            if (!string.Equals(ns, "MegaCrit.Sts2.Core.Models.Monsters", StringComparison.Ordinal))
            {
                monsterType = null;
                return false;
            }

            monsterType = reader.GetString(type.Name);
            return true;
        }
    }

    private static IReadOnlyList<IlInstructionEvidence> Decode(
        byte[] il,
        MetadataReader reader)
    {
        List<IlInstructionEvidence> instructions = [];
        int offset = 0;
        while (offset < il.Length)
        {
            int instructionOffset = offset;
            ushort opcodeValue = il[offset++];
            if (opcodeValue == 0xfe)
            {
                if (offset >= il.Length)
                    throw new BadImageFormatException("truncated two-byte IL opcode");
                opcodeValue = (ushort)(0xfe00 | il[offset++]);
            }

            if (!OpCodesByValue.TryGetValue(opcodeValue, out OpCode opcode))
                throw new BadImageFormatException($"unknown IL opcode 0x{opcodeValue:x4}");

            string? operand = ReadOperand(opcode, il, ref offset, reader);
            instructions.Add(new IlInstructionEvidence(
                instructionOffset,
                opcode.Name ?? $"0x{opcodeValue:x4}",
                operand));
        }
        return instructions;
    }

    private static string? ReadOperand(
        OpCode opcode,
        byte[] il,
        ref int offset,
        MetadataReader reader)
    {
        switch (opcode.OperandType)
        {
            case OperandType.InlineNone:
                return null;
            case OperandType.ShortInlineI:
                return ((sbyte)il[offset++]).ToString();
            case OperandType.InlineI:
            {
                int value = BitConverter.ToInt32(il, offset);
                offset += 4;
                return value.ToString();
            }
            case OperandType.InlineI8:
            {
                long value = BitConverter.ToInt64(il, offset);
                offset += 8;
                return value.ToString();
            }
            case OperandType.ShortInlineR:
            {
                float value = BitConverter.ToSingle(il, offset);
                offset += 4;
                return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            case OperandType.InlineR:
            {
                double value = BitConverter.ToDouble(il, offset);
                offset += 8;
                return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            case OperandType.ShortInlineVar:
                return il[offset++].ToString();
            case OperandType.InlineVar:
            {
                ushort value = BitConverter.ToUInt16(il, offset);
                offset += 2;
                return value.ToString();
            }
            case OperandType.ShortInlineBrTarget:
            {
                sbyte delta = (sbyte)il[offset++];
                return $"IL_{offset + delta:x4}";
            }
            case OperandType.InlineBrTarget:
            {
                int delta = BitConverter.ToInt32(il, offset);
                offset += 4;
                return $"IL_{offset + delta:x4}";
            }
            case OperandType.InlineSwitch:
            {
                int count = BitConverter.ToInt32(il, offset);
                offset += 4;
                int baseOffset = offset + count * 4;
                string[] targets = new string[count];
                for (int index = 0; index < count; index++)
                {
                    int delta = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    targets[index] = $"IL_{baseOffset + delta:x4}";
                }
                return string.Join(",", targets);
            }
            case OperandType.InlineString:
            {
                int token = BitConverter.ToInt32(il, offset);
                offset += 4;
                try
                {
                    return "\"" + reader.GetUserString(
                        MetadataTokens.UserStringHandle(token & 0x00ffffff)) + "\"";
                }
                catch
                {
                    return $"string-token=0x{token:x8}";
                }
            }
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineType:
            case OperandType.InlineTok:
            case OperandType.InlineSig:
            {
                int token = BitConverter.ToInt32(il, offset);
                offset += 4;
                return ResolveToken(reader, token);
            }
            default:
                throw new NotSupportedException(
                    $"Unsupported IL operand type {opcode.OperandType} for {opcode.Name}.");
        }
    }

    private static string ResolveToken(MetadataReader reader, int token)
    {
        try
        {
            EntityHandle handle = MetadataTokens.EntityHandle(token);
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => FormatMethod(reader, (MethodDefinitionHandle)handle),
                HandleKind.MemberReference => FormatMemberReference(reader, (MemberReferenceHandle)handle),
                HandleKind.MethodSpecification => FormatMethodSpecification(
                    reader,
                    (MethodSpecificationHandle)handle),
                HandleKind.FieldDefinition => FormatField(reader, (FieldDefinitionHandle)handle),
                HandleKind.TypeDefinition => FormatType(reader, (TypeDefinitionHandle)handle),
                HandleKind.TypeReference => FormatType(reader, (TypeReferenceHandle)handle),
                _ => $"{handle.Kind}:0x{token:x8}",
            };
        }
        catch
        {
            return $"token=0x{token:x8}";
        }
    }

    private static string FormatMethod(MetadataReader reader, MethodDefinitionHandle handle)
    {
        MethodDefinition method = reader.GetMethodDefinition(handle);
        return FormatType(reader, method.GetDeclaringType())
            + "::" + reader.GetString(method.Name);
    }

    private static string FormatField(MetadataReader reader, FieldDefinitionHandle handle)
    {
        FieldDefinition field = reader.GetFieldDefinition(handle);
        return FormatType(reader, field.GetDeclaringType())
            + "::" + reader.GetString(field.Name);
    }

    private static string FormatMemberReference(MetadataReader reader, MemberReferenceHandle handle)
    {
        MemberReference member = reader.GetMemberReference(handle);
        return FormatParent(reader, member.Parent)
            + "::" + reader.GetString(member.Name);
    }

    private static string FormatMethodSpecification(
        MetadataReader reader,
        MethodSpecificationHandle handle)
    {
        MethodSpecification specification = reader.GetMethodSpecification(handle);
        return specification.Method.Kind switch
        {
            HandleKind.MethodDefinition => FormatMethod(
                reader,
                (MethodDefinitionHandle)specification.Method) + "<spec>",
            HandleKind.MemberReference => FormatMemberReference(
                reader,
                (MemberReferenceHandle)specification.Method) + "<spec>",
            _ => $"{specification.Method.Kind}:method-spec",
        };
    }

    private static string FormatParent(MetadataReader reader, EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.TypeDefinition => FormatType(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => FormatType(reader, (TypeReferenceHandle)handle),
            HandleKind.MethodDefinition => FormatMethod(reader, (MethodDefinitionHandle)handle),
            _ => handle.Kind.ToString(),
        };

    private static string FormatType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string ns = reader.GetString(type.Namespace);
        string name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string FormatType(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string ns = reader.GetString(type.Namespace);
        string name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}

internal sealed record MonsterMoveIlEvidence(
    int SchemaVersion,
    string AssemblySha256,
    IReadOnlyList<MonsterMoveMethodIl> Methods);

internal sealed record MonsterMoveMethodIl(
    string MonsterType,
    string DeclaringType,
    string Method,
    int MetadataToken,
    IReadOnlyList<IlInstructionEvidence> Instructions);

internal sealed record IlInstructionEvidence(
    int Offset,
    string OpCode,
    string? Operand);
