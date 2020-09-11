using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Struct representing an <see cref="AnimationCurve"/> that can be used with the Burst Compiler.
/// </summary>
public struct NativeAnimationCurve : IDisposable
{
    struct AnimationData
    {
        public BlobArray<Keyframe> keyframes;

        // This is a hack to help stupid linear interval search, ideally we should find a better way to land on the interval without linear search in the first place.
        public BlobArray<float> soaTimes;

        // More hack to help the stupid linear search, cached index is for my observation that we usually evaluate on the same interval or the next one. 
        // But ideally we have to find better way to land on the correct interval fast without this. (interval tree?)
        public BlobArray<int> cachedIndex;
    }

    readonly struct KeyframeTimeComparer : IComparer<Keyframe>
    {
        public int Compare(Keyframe x, Keyframe y)
            => x.time.CompareTo(y.time);
    }

    BlobAssetReference<AnimationData> animationDataBlob;

    /// <summary> Used for caching optimization. Each thread would get its own cache area which is selected based on this integer. </summary>
    [NativeSetThreadIndex]
    readonly int threadIndex;

    public int Length
        => animationDataBlob.Value.keyframes.Length;

    /// <summary> Create a new NativeAnimationCurve based on the keyframes from the <paramref name="animationCurve">. </summary>
    public NativeAnimationCurve(AnimationCurve animationCurve, Allocator allocator)
    {
        using (var sortedKeyframes = new NativeArray<Keyframe>(animationCurve.keys, Allocator.Temp))
        {
            for (int i = 0, n = sortedKeyframes.Length; i < n; i++)
                if (sortedKeyframes[i].weightedMode != WeightedMode.None)
                    throw new NotSupportedException("Found a keyframe in the curve that has a weighted node. This is not supported.");

            sortedKeyframes.Sort(default(KeyframeTimeComparer));

            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<AnimationData>();

                var keyframes = builder.Allocate(ref root.keyframes, sortedKeyframes.Length);
                var soaTimes = builder.Allocate(ref root.soaTimes, sortedKeyframes.Length);
                var cachedIndex = builder.Allocate(ref root.cachedIndex, JobsUtility.MaxJobThreadCount);

                for (int i = 0; i < sortedKeyframes.Length; i++)
                {
                    keyframes[i] = sortedKeyframes[i];
                    soaTimes[i] = sortedKeyframes[i].time;
                }

                for (int i = 0; i < JobsUtility.MaxJobThreadCount; i++)
                    cachedIndex[i] = 0;

                animationDataBlob = builder.CreateBlobAssetReference<AnimationData>(allocator);
            }
        }

        threadIndex = 0;
    }

    public float Evaluate(float time)
    {
        ref var animationData = ref animationDataBlob.Value;

        int n = animationData.soaTimes.Length;
        for (int i = animationData.cachedIndex[threadIndex], count = 0; count < n; count++, i = (i + 1) % (n - 1))
        {
            if (time < animationData.soaTimes[i] || time >= animationData.soaTimes[i + 1])
                continue;

            ref var left = ref animationData.keyframes[i];
            ref var right = ref animationData.keyframes[i + 1];
            animationData.cachedIndex[threadIndex] = i;

            return EvaluateInternal(time, left.time, left.value, left.outTangent, right.time, right.value, right.inTangent);
        }

        return float.NaN;
    }

    static float EvaluateInternal(float iInterp, float iLeft, float vLeft, float tLeft, float iRight, float vRight, float tRight)
    {
        var t = math.unlerp(iLeft, iRight, iInterp);
        var scale = iRight - iLeft;

        var parameters = new float4(
            t * t * t,
            t * t,
            t,
            1
        );

        var hermiteBasis = new float4x4(
            +2, -2, +1, +1,
            -3, +3, -2, -1,
            +0, +0, +1, +0,
            +1, +0, +0, +0
        );

        var control = new float4(vLeft, vRight, tLeft, tRight) * new float4(1, 1, scale, scale);
        var basisWithParams = math.mul(parameters, hermiteBasis);
        var hermiteBlend = control * basisWithParams;
        return math.csum(hermiteBlend);
    }

    public void Dispose()
        => animationDataBlob.Dispose();
}
