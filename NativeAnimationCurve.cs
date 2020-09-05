using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Struct representing an <see cref="AnimationCurve"/> that can be used with the Burst Compiler.
/// </summary>
public struct NativeAnimationCurve
{
    readonly struct KeyframeTimeComparer : IComparer<Keyframe>
    {
        public int Compare(Keyframe x, Keyframe y)
            => x.time.CompareTo(y.time);
    }

    FixedList128<float4> keyframes;

    public int Length
        => keyframes.Length;

    /// <summary> Create a new NativeAnimationCurve based on the keyframes from the <paramref name="animationCurve">. </summary>
    public NativeAnimationCurve(AnimationCurve animationCurve)
    {
        keyframes = default;
        
        using (var inputKeyframes = new NativeArray<Keyframe>(animationCurve.keys, Allocator.Temp))
        {
            int n = inputKeyframes.Length;
            keyframes.Length = n;

            inputKeyframes.Sort(default(KeyframeTimeComparer));

            for (int i = 0; i < n; i++)
            {
                var keyframe = inputKeyframes[i];
                
                if (keyframe.weightedMode != WeightedMode.None)
                    throw new NotSupportedException("Found a keyframe in the curve that has a weighted node. This is not supported.");
                
                keyframes[i] = new float4(keyframe.time, keyframe.value, keyframe.inTangent, keyframe.outTangent);
            }
        }
    }

    public float Evaluate(float time)
    {
        int n = keyframes.Length;

        for (int i = 0; i < n; ++i)
        {
            ref float4 left = ref keyframes.ElementAt(i);
            ref float4 right = ref keyframes.ElementAt(i + 1);

            if (math.any(new bool2(time < left.x, time >= right.x)))
                continue;

            return EvaluateInternal(time, left.x, left.y, left.w, right.x, right.y, right.z);
        }

        return float.NaN;
    }

    static float EvaluateInternal(float time, float leftTime, float leftValue, float leftTangent, float rightTime, float rightValue, float rightTangent)
    {
        float t = math.unlerp(leftTime, rightTime, time);
        float scale = rightTime - leftTime;

        float4 parameters = new float4(
            t * t * t,
            t * t,
            t,
            1
        );

        float4x4 hermiteBasis = new float4x4(
            +2, -2, +1, +1,
            -3, +3, -2, -1,
            +0, +0, +1, +0,
            +1, +0, +0, +0
        );

        float4 control = new float4(
            leftValue,
            rightValue,
            leftTangent * scale,
            rightTangent * scale
        );

        float4 basisWithParams = math.mul(parameters, hermiteBasis);

        float4 hermiteBlend = control * basisWithParams;

        return math.csum(hermiteBlend);
    }
}
