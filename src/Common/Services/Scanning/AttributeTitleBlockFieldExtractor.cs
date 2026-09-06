using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 属性图框字段提取器：按关键字匹配块属性标签（Tag），提取并清理属性值。
/// 关键字匹配规则：标签去除首尾空白后与关键字忽略大小写比较，相等或标签包含关键字即命中；
/// 同一字段按关键字顺序取第一个命中的属性，先到先得。
/// </summary>
public static class AttributeTitleBlockFieldExtractor
{
    /// <summary>读取块参照的全部属性（标签 + 清理格式控制字符后的值），不可见属性跳过。</summary>
    public static List<(string Tag, string Value)> ReadAttributes(Transaction tr, BlockReference blockRef)
    {
        var result = new List<(string Tag, string Value)>();
        foreach (ObjectId attributeId in blockRef.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attribute)
            {
                continue;
            }

            var tag = attribute.Tag?.Trim() ?? "";
            if (tag.Length == 0)
            {
                continue;
            }

            result.Add((tag, CadTextExtractor.CleanText(attribute.TextString)));
        }

        return result;
    }

    /// <summary>
    /// 从属性列表中按关键字提取字段值：遍历关键字顺序，每个关键字再按属性出现顺序找第一个命中标签。
    /// 未命中返回空字符串。
    /// </summary>
    public static string ExtractField(
        IReadOnlyList<(string Tag, string Value)> attributes,
        IReadOnlyList<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            var normalized = keyword?.Trim() ?? "";
            if (normalized.Length == 0)
            {
                continue;
            }

            foreach (var attribute in attributes)
            {
                if (TagMatchesKeyword(attribute.Tag, normalized) && attribute.Value.Length > 0)
                {
                    return attribute.Value;
                }
            }
        }

        return "";
    }

    /// <summary>标签是否命中关键字：忽略大小写的相等或包含匹配。</summary>
    public static bool TagMatchesKeyword(string tag, string keyword)
    {
        return string.Equals(tag, keyword, StringComparison.OrdinalIgnoreCase)
            || (tag.Length > keyword.Length
                && tag.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// 判断标签被哪个字段命中：按设置中的关键字映射逐字段检测，返回字段键；
    /// 未命中任何字段返回 null。供属性预览高亮使用。
    /// </summary>
    public static string? FindMatchedFieldKey(
        string tag,
        IReadOnlyDictionary<string, List<string>> fieldKeywords)
    {
        foreach (var fieldKey in AppSettingsStore.AttributeTitleBlockFieldKeys)
        {
            if (fieldKeywords.TryGetValue(fieldKey, out var keywords))
            {
                foreach (var keyword in keywords)
                {
                    var normalized = keyword?.Trim() ?? "";
                    if (normalized.Length > 0 && TagMatchesKeyword(tag, normalized))
                    {
                        return fieldKey;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>按字段键提取全部图框字段值；返回字典（字段键 → 属性值），缺失字段值为空字符串。</summary>
    public static Dictionary<string, string> ExtractAllFields(
        IReadOnlyList<(string Tag, string Value)> attributes,
        IReadOnlyDictionary<string, List<string>> fieldKeywords)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fieldKey in AppSettingsStore.AttributeTitleBlockFieldKeys)
        {
            var keywords = fieldKeywords.TryGetValue(fieldKey, out var list) && list != null
                ? list
                : new List<string>();
            result[fieldKey] = ExtractField(attributes, keywords);
        }

        return result;
    }
}
