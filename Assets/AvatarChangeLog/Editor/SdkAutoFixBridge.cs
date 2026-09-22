using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvatarChangeLog
{
    // Observe SDK button input without replacing its action or introducing an SDK assembly dependency.
    [InitializeOnLoad]
    internal static class SdkAutoFixBridge
    {
        static Type panelType;
        static MethodInfo getBuilder;
        static PropertyInfo selectedAvatar;
        static readonly Dictionary<EditorWindow, Observer> observers = new Dictionary<EditorWindow, Observer>();

        static SdkAutoFixBridge() { EditorApplication.delayCall += Initialize; }

        static void Initialize()
        {
            Type avatarApi = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                panelType = panelType ?? assembly.GetType("VRCSdkControlPanel");
                avatarApi = avatarApi ?? assembly.GetType("VRC.SDK3A.Editor.IVRCSdkAvatarBuilderApi");
            }
            if (panelType == null || avatarApi == null) return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            var enabled = panelType.GetEvent("OnSdkPanelEnable", flags);
            var disabled = panelType.GetEvent("OnSdkPanelDisable", flags);
            selectedAvatar = avatarApi.GetProperty("SelectedAvatar");
            foreach (var method in panelType.GetMethods(flags))
                if (method.Name == "TryGetBuilder" && method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 1)
                { getBuilder = method.MakeGenericMethod(avatarApi); break; }
            if (getBuilder == null || selectedAvatar == null || enabled == null || disabled == null ||
                enabled.EventHandlerType != typeof(EventHandler) || disabled.EventHandlerType != typeof(EventHandler)) return;
            enabled.AddEventHandler(null, new EventHandler(OnEnabled));
            disabled.AddEventHandler(null, new EventHandler(OnDisabled));
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>()) Attach(window);
        }

        static void OnEnabled(object sender, EventArgs _) { Attach(sender as EditorWindow); }
        static void OnDisabled(object sender, EventArgs _)
        {
            if (sender is EditorWindow window && observers.TryGetValue(window, out var observer))
            { observer.Dispose(); observers.Remove(window); }
        }
        static void Attach(EditorWindow window)
        {
            if (window && panelType.IsInstanceOfType(window) && !observers.ContainsKey(window))
                observers.Add(window, new Observer(window.rootVisualElement, Begin));
        }

        static Action Begin()
        {
            if (!RecordingService.Running || RecordingService.Paused) return null;
            try
            {
                var arguments = new object[] { null };
                if (!(bool)getBuilder.Invoke(null, arguments) || arguments[0] == null) return null;
                return RecordingService.BeginAutoFix(selectedAvatar.GetValue(arguments[0]) as GameObject);
            }
            catch (Exception)
            {
                // An incompatible SDK must not interrupt its button or ordinary change recording.
                return null;
            }
        }

        internal sealed class Observer : IDisposable
        {
            readonly VisualElement root;
            readonly Func<Action> begin;
            Button pressed;
            int pointerId;

            internal Observer(VisualElement root, Func<Action> begin)
            {
                this.root = root; this.begin = begin;
                root.RegisterCallback<PointerDownEvent>(OnDown, TrickleDown.TrickleDown);
                root.RegisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
                root.RegisterCallback<PointerCancelEvent>(OnCancel, TrickleDown.TrickleDown);
                root.RegisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
            }

            static Button FixButton(IEventHandler target)
            {
                var element = target as VisualElement;
                var button = element as Button ?? element?.GetFirstAncestorOfType<Button>();
                return button != null && button.enabledInHierarchy && button.text == "Auto Fix" ? button : null;
            }
            void OnDown(PointerDownEvent e)
            {
                ClearPress();
                pressed = e.pointerId == PointerId.mousePointerId && e.button == 0 && e.modifiers == EventModifiers.None ? FixButton(e.target) : null;
                pointerId = e.pointerId;
                if (pressed == null) return;
                // In Unity 2022 captured pointer events bypass ancestors. Observe the target's
                // callbacks too. Mouse PointerUp precedes Clickable's compatibility MouseUp action.
                pressed.RegisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
                pressed.RegisterCallback<PointerCancelEvent>(OnCancel, TrickleDown.TrickleDown);
                pressed.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            }
            void OnCancel(PointerCancelEvent e) { if (e.pointerId == pointerId) ClearPress(); }
            void OnCaptureOut(PointerCaptureOutEvent e) { if (e.pointerId == pointerId) ClearPress(); }
            void ClearPress()
            {
                if (pressed == null) return;
                pressed.UnregisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
                pressed.UnregisterCallback<PointerCancelEvent>(OnCancel, TrickleDown.TrickleDown);
                pressed.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
                pressed = null;
            }
            void OnUp(PointerUpEvent e)
            {
                if (e.button != 0 || e.pointerId != pointerId) return;
                var button = pressed; ClearPress();
                if (button != null && button == FixButton(e.target) && button.worldBound.Contains(e.position)) Start();
            }
            void OnSubmit(NavigationSubmitEvent e) { if (FixButton(e.target) != null) Start(); }
            void Start()
            {
                var complete = begin();
                if (complete != null) EditorApplication.delayCall += () => complete();
            }
            public void Dispose()
            {
                ClearPress();
                root.UnregisterCallback<PointerDownEvent>(OnDown, TrickleDown.TrickleDown);
                root.UnregisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
                root.UnregisterCallback<PointerCancelEvent>(OnCancel, TrickleDown.TrickleDown);
                root.UnregisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
            }
        }
    }
}
