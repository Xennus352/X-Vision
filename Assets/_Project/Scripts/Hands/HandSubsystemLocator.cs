using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace CardboardXR.Hands
{
    // Finds the running XR Hands subsystem for the glove visualizer and the fingertip touch
    // detector, with the same static-list-plus-requery-once-a-second pattern as
    // XRHandTrackingEvents (and the solar system controller). A subsystem that already exists
    // keeps being returned; when none is running, Get returns null without spamming the registry.
    static class HandSubsystemLocator
    {
        const float k_RequerySeconds = 1f;

        static readonly List<XRHandSubsystem> s_Subsystems = new();
        static XRHandSubsystem s_Current;
        static float s_NextQuery;

        public static XRHandSubsystem Get()
        {
            if (s_Current != null && s_Current.running)
                return s_Current;
            s_Current = null;
            if (Time.unscaledTime < s_NextQuery)
                return null;
            s_NextQuery = Time.unscaledTime + k_RequerySeconds;
            s_Subsystems.Clear();
            SubsystemManager.GetSubsystems(s_Subsystems);
            foreach (var subsystem in s_Subsystems)
            {
                if (subsystem.running)
                {
                    s_Current = subsystem;
                    return s_Current;
                }
            }
            return null;
        }
    }
}
