# NativeAnimationCurve

Cleaned up version of [5argon/JobAnimationCurve](https://github.com/5argon/JobAnimationCurve), with a couple of optimizations and same overall features and drawbacks (no weighted tangents and no wrapping).

This is currently faster than an AnimationCurve when used in conjunction with the Burst Compiler, but significantly slower in Mono.
