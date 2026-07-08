using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PFound.Signaling.Editor
{
    /// <summary>
    /// Inspector drawer for <see cref="SignalKey"/>: a dropdown enumerating every concrete
    /// <see cref="SignalBase"/>-derived type so designers can author a signal reference without a
    /// compile-time generic parameter. The picked type's assembly-qualified name is stored, which
    /// is exactly what <see cref="SignalKey.Create{T}"/> stores, so authored and code keys match.
    /// </summary>
    [CustomPropertyDrawer(typeof(SignalKey))]
    public sealed class SignalKeyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty typeNameProp = property.FindPropertyRelative("_typeName");

            // Enumerate concrete signal types (TypeCache refreshes on recompile — no manual scan).
            var signalTypes = new List<Type>();
            foreach (Type t in TypeCache.GetTypesDerivedFrom<SignalBase>())
                if (!t.IsAbstract) signalTypes.Add(t);
            signalTypes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            var labels = new List<GUIContent> { new GUIContent("<None>") };
            for (int i = 0; i < signalTypes.Count; i++)
                labels.Add(new GUIContent(signalTypes[i].Name, signalTypes[i].FullName));

            string current = typeNameProp.stringValue;
            int selected = 0;
            for (int i = 0; i < signalTypes.Count; i++)
            {
                if (string.Equals(signalTypes[i].AssemblyQualifiedName, current, StringComparison.Ordinal))
                {
                    selected = i + 1;
                    break;
                }
            }

            // A stored type that no longer exists (renamed/removed) — surface it rather than wipe it.
            bool missing = selected == 0 && !string.IsNullOrEmpty(current);
            if (missing)
            {
                labels.Add(new GUIContent($"(missing) {current}"));
                selected = labels.Count - 1;
            }

            EditorGUI.BeginProperty(position, label, property);
            int chosen = EditorGUI.Popup(position, label, selected, labels.ToArray());
            if (chosen != selected)
            {
                if (chosen == 0)
                    typeNameProp.stringValue = string.Empty;                       // <None>
                else if (!missing || chosen != labels.Count - 1)
                    typeNameProp.stringValue = signalTypes[chosen - 1].AssemblyQualifiedName;
            }
            EditorGUI.EndProperty();
        }
    }
}
