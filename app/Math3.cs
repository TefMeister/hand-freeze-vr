// Math3.cs -- the little 3D arithmetic of the SPOTS tab (2026-11-25): vectors, turns (unit quaternions x y z w, the same
// convention as the layer and OpenXR) and poses (a turn and a place). Metres, right-handed, y up.
using System;

struct V3 {
    public double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
    public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    public static V3 operator *(V3 a, double k) { return new V3(a.X * k, a.Y * k, a.Z * k); }
    public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
    public static double Dist(V3 a, V3 b) { return (a - b).Length; }
    public static readonly V3 Zero = new V3(0, 0, 0);
}

struct Quat {
    public double X, Y, Z, W;
    public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
    public static readonly Quat One = new Quat(0, 0, 0, 1);
    public static Quat operator *(Quat a, Quat b) {
        return new Quat(a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y, a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W, a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    }
    public Quat Inverse { get { return new Quat(-X, -Y, -Z, W); } }
    public Quat Unit { get { double n = Math.Sqrt(X * X + Y * Y + Z * Z + W * W); return n < 1e-12 ? One : new Quat(X / n, Y / n, Z / n, W / n); } }
    public V3 Turn(V3 v) {
        Quat r = this * new Quat(v.X, v.Y, v.Z, 0) * Inverse;
        return new V3(r.X, r.Y, r.Z);
    }
    // a turn of `deg` degrees about an axis (need not be unit length)
    public static Quat About(V3 axis, double deg) {
        double n = axis.Length; if (n < 1e-12) return One;
        double h = deg * Math.PI / 360.0, s = Math.Sin(h) / n;
        return new Quat(axis.X * s, axis.Y * s, axis.Z * s, Math.Cos(h));
    }
    // the angle between two turns, degrees
    public static double Apart(Quat a, Quat b) {
        double d = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        return 2 * Math.Acos(Math.Min(1.0, d)) * 180.0 / Math.PI;
    }
}

struct Pose {
    public Quat Q; public V3 P;
    public Pose(Quat q, V3 p) { Q = q; P = p; }
    public static readonly Pose One = new Pose(Quat.One, V3.Zero);
    // b, given in a's frame, in a's parent frame
    public static Pose operator *(Pose a, Pose b) { return new Pose((a.Q * b.Q).Unit, a.P + a.Q.Turn(b.P)); }
    public Pose Inverse { get { Quat i = Q.Inverse; return new Pose(i, i.Turn(new V3(-P.X, -P.Y, -P.Z))); } }
    public V3 Apply(V3 v) { return P + Q.Turn(v); }
    // a turn about a point of the parent frame
    public static Pose AboutPoint(V3 point, Quat turn) { return new Pose(turn, point - turn.Turn(point)); }
    // the level frame of a head: at the head, turned only left / right the way it looks (x right, y up, -z ahead)
    public static Pose Level(Pose head) {
        V3 f = head.Q.Turn(new V3(0, 0, -1));
        double yaw = Math.Atan2(-f.X, -f.Z) * 180.0 / Math.PI;
        return new Pose(Quat.About(new V3(0, 1, 0), yaw), head.P);
    }
}
