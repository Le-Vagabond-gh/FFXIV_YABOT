using Dalamud.Interface;
using ECommons.ImGuiMethods;
using ECommons.ImGuiMethods.TerritorySelection;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Reflection;

namespace YABOT.FeaturesSetup
{
    public static class FeatureConfigEditor
    {
        // Generic radio-button picker for any enum config field. Labels come from a [Description]
        // attribute on each enum member when present, otherwise the member name.
        public static bool RadioEnumEditor(string name, ref object configOption)
        {
            if (configOption is not Enum) return false;

            var type = configOption.GetType();
            var changed = false;

            if (!string.IsNullOrEmpty(name))
                ImGui.TextUnformatted(name);

            foreach (var value in Enum.GetValues(type))
            {
                if (ImGui.RadioButton($"{EnumLabel(type, value!)}##{name}_{value}", configOption.Equals(value)))
                {
                    configOption = value!;
                    changed = true;
                }
            }

            return changed;
        }

        private static string EnumLabel(Type type, object value)
        {
            var member = type.GetMember(value.ToString()!);
            if (member.Length > 0 && member[0].GetCustomAttribute<DescriptionAttribute>() is { } desc)
                return desc.Description;
            return value.ToString()!;
        }

        public static bool ColorEditor(string name, ref object configOption)
        {
            switch (configOption)
            {
                case Vector4 v4 when ImGui.ColorEdit4(name, ref v4):
                    configOption = v4;
                    return true;
                case Vector3 v3 when ImGui.ColorEdit3(name, ref v3):
                    configOption = v3;
                    return true;
                default:
                    return false;
            }
        }

        public static bool SimpleColorEditor(string name, ref object configOption)
        {
            switch (configOption)
            {
                case Vector4 v4 when ImGui.ColorEdit4(name, ref v4, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.AlphaBar):
                    configOption = v4;
                    return true;
                default:
                    return false;
            }
        }

        public static bool TerritorySelectionEditor(string name, ref object configOption)
        {
            if (configOption is List<uint> territories)
            {
                if (ImGuiEx.IconButton(FontAwesomeIcon.List))
                {
                    var x = new TerritorySelector(territories, (terr, selectedTerritories) =>
                    {
                        territories.Clear();
                        territories.AddRange(selectedTerritories);
                    })
                    {
                        SelectedCategory = TerritorySelector.Category.All,
                        ExtraColumns = [TerritorySelector.Column.ID, TerritorySelector.Column.IntendedUse],
                    };
                    return true;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"Zone Whitelist ({territories.Count} territories selected)");
            }
            return false;
        }
    }
}
