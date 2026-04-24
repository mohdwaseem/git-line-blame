// GitBlameAdornmentLayer.cs intentionally left empty.
// The adornment layer is now declared as an instance field on GitBlameViewListener
// so that MEF v1 (used by Visual Studio) can discover it as part of an
// instantiable MEF part. Static classes and static fields are NOT discoverable
// by MEF v1 — using a static class was a bug that prevented the layer from
// ever being registered, making GetAdornmentLayer("GitLineBlame") throw.
