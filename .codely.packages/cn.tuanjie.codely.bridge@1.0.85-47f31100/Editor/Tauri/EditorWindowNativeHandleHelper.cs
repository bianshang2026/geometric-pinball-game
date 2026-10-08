#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
using System;
using System.Reflection;
using UnityEditor;

namespace Cn.Tuanjie.Codely.Editor
{
    public static class EditorWindowNativeHandleHelper
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static IntPtr GetGUIViewHandle(EditorWindow window)
        {
            if (window == null)
            {
                return IntPtr.Zero;
            }

            var parentField = typeof(EditorWindow).GetField("m_Parent", InstanceFlags);
            object guiView = parentField?.GetValue(window);
            if (guiView == null)
            {
                return IntPtr.Zero;
            }

            IntPtr nativeHandle = TryGetNativeHandleProperty(guiView);
            if (nativeHandle != IntPtr.Zero)
            {
                return nativeHandle;
            }

            return TryGetViewPtrField(guiView);
        }

        private static IntPtr TryGetNativeHandleProperty(object guiView)
        {
            var nativeHandleProperty = guiView.GetType().GetProperty("nativeHandle", InstanceFlags);
            if (nativeHandleProperty == null)
            {
                return IntPtr.Zero;
            }

            return TryConvertToIntPtr(nativeHandleProperty.GetValue(guiView, null));
        }

        private static IntPtr TryGetViewPtrField(object guiView)
        {
            var viewPtrField = GetFieldInTypeHierarchy(guiView.GetType(), "m_ViewPtr");
            if (viewPtrField == null)
            {
                return IntPtr.Zero;
            }

            return TryConvertToIntPtr(viewPtrField.GetValue(guiView));
        }

        private static IntPtr TryConvertToIntPtr(object value)
        {
            if (value == null)
            {
                return IntPtr.Zero;
            }

            if (value is IntPtr ptr)
            {
                return ptr;
            }

            Type valueType = value.GetType();
            foreach (string memberName in new[] { "m_IntPtr", "m_Ptr", "m_Value", "value" })
            {
                var field = GetFieldInTypeHierarchy(valueType, memberName);
                if (field != null && field.FieldType == typeof(IntPtr))
                {
                    return (IntPtr)field.GetValue(value);
                }

                var property = valueType.GetProperty(memberName, InstanceFlags);
                if (property != null && property.PropertyType == typeof(IntPtr))
                {
                    return (IntPtr)property.GetValue(value, null);
                }
            }

            foreach (var field in valueType.GetFields(InstanceFlags))
            {
                if (field.FieldType == typeof(IntPtr))
                {
                    return (IntPtr)field.GetValue(value);
                }
            }

            return IntPtr.Zero;
        }

        private static FieldInfo GetFieldInTypeHierarchy(Type type, string name)
        {
            while (type != null)
            {
                var field = type.GetField(name, InstanceFlags);
                if (field != null)
                {
                    return field;
                }

                type = type.BaseType;
            }

            return null;
        }
    }
}
#endif
