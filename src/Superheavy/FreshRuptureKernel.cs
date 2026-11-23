using System;
using System.Collections.Generic;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Independent polygon subtraction for new material/section profiles.
    // No dependency on SurfaceDamageCut or the old circular damage-radius policy.
    public static class FreshRuptureKernel
    {
        public struct Vertex
        {
            public Vector3 Position,Normal;public Vector4 Tangent;public Vector2 U0,U1,U2,U3;
            public static Vertex Lerp(Vertex a,Vertex b,float t)=>new Vertex{Position=a.Position+(b.Position-a.Position)*t,Normal=a.Normal+(b.Normal-a.Normal)*t,Tangent=a.Tangent+(b.Tangent-a.Tangent)*t,U0=a.U0+(b.U0-a.U0)*t,U1=a.U1+(b.U1-a.U1)*t,U2=a.U2+(b.U2-a.U2)*t,U3=a.U3+(b.U3-a.U3)*t};
        }
        public struct Triangle {public Vertex A,B,C;public Triangle(Vertex a,Vertex b,Vertex c){A=a;B=b;C=c;}}
        public struct Edge {public Vertex A,B;public Edge(Vertex a,Vertex b){A=a;B=b;}}
        public sealed class Wound
        {
            public Vector3 Center,Axis;public Vector3[] Normals;public float[] Distances;public int RadialPlanes;
            public float Distance(int i,Vector3 p)=>Vector3.Dot(Normals[i],p-Center)-Distances[i];
            public bool Boundary(Vector3 p)
            {bool boundary=false;for(int i=0;i<Normals.Length;i++){float d=Distance(i,p);if(d>2e-5f)return false;if(i<RadialPlanes&&Math.Abs(d)<2e-5f)boundary=true;}return boundary;}
        }
        public static float Noise(int seed,int index)
        {unchecked{uint x=(uint)(seed+index*73471);x^=x>>15;x*=0x2c1b3c6d;x^=x>>12;x*=0x297a2d39;x^=x>>15;return(x&0xffffff)/16777215f;}}
        public static int StableHash(string value){unchecked{int hash=17;foreach(char c in value)hash=hash*31+c;return hash;}}
        public static Wound Prepare(Vector3 center,Vector3 axis,Vector3 longitudinal,float radius,float stretch,float depth,int sides,int seed)
        {
            if(radius<=0||depth<=0||sides<5||sides>16||axis.sqrMagnitude<1e-10f)throw new ArgumentException("Invalid owned wound dimensions.");
            axis=axis.normalized;var u=longitudinal-axis*Vector3.Dot(longitudinal,axis);if(u.sqrMagnitude<1e-8f)u=Vector3.Cross(axis,Math.Abs(axis.y)<.8f?Vector3.up:Vector3.right);u=u.normalized;var v=Vector3.Cross(axis,u);
            var w=new Wound{Center=center,Axis=axis,RadialPlanes=sides,Normals=new Vector3[sides+2],Distances=new float[sides+2]};float phase=Noise(seed,19)*6.2831853f;
            for(int i=0;i<sides;i++){float angle=phase+i*6.2831853f/sides;w.Normals[i]=u*((float)Math.Cos(angle)/Math.Max(.25f,stretch))+v*(float)Math.Sin(angle);w.Distances[i]=radius*(.79f+.34f*Noise(seed,i));}
            w.Normals[sides]=axis;w.Distances[sides]=depth;w.Normals[sides+1]=-axis;w.Distances[sides+1]=depth;return w;
        }
        private static List<Vertex> Clip(List<Vertex> polygon,Wound w,int plane,bool inside)
        {
            var output=new List<Vertex>();if(polygon.Count==0)return output;var a=polygon[polygon.Count-1];float da=w.Distance(plane,a.Position)*(inside?1:-1);
            foreach(var b in polygon){float db=w.Distance(plane,b.Position)*(inside?1:-1);bool ai=da<=0,bi=db<=0;if(ai!=bi)output.Add(Vertex.Lerp(a,b,da/(da-db)));if(bi)output.Add(b);a=b;da=db;}return output;
        }
        private static void Fan(List<Vertex> p,ICollection<Triangle> output)
        {for(int i=1;i<p.Count-1;i++)if(Vector3.Cross(p[i].Position-p[0].Position,p[i+1].Position-p[0].Position).sqrMagnitude>1e-16f)output.Add(new Triangle(p[0],p[i],p[i+1]));}
        public static void Subtract(Triangle source,Wound wound,List<Triangle> output,List<Edge> boundary)
        {
            var rest=new List<Vertex>{source.A,source.B,source.C};
            for(int i=0;i<wound.Normals.Length&&rest.Count>=3;i++){Fan(Clip(rest,wound,i,false),output);rest=Clip(rest,wound,i,true);}
            if(boundary==null)return;
            // The final removed polygon has the complete clipped boundary.
            // Earlier retained polygons can span several later cutting planes.
            for(int i=0;i<rest.Count;i++)Add(rest[i],rest[(i+1)%rest.Count],wound,boundary);
        }
        // Keep an untouched triangle verbatim when a wound removes no area.
        // The original entry point is retained for the existing Bastion path.
        public static bool SubtractIfRemoved(Triangle source,Wound wound,List<Triangle> output,List<Edge> boundary)
        {return SubtractIfRemoved(source,wound,(IList<Triangle>)output,boundary);}
        public static bool SubtractIfRemoved(Triangle source,Wound wound,IList<Triangle> output,List<Edge> boundary)
        {
            // Most nearby triangles are still outside at least one wound
            // plane. Reject them without allocating clipping polygons.
            for(int i=0;i<wound.Normals.Length;i++)if(wound.Distance(i,source.A.Position)>0&&wound.Distance(i,source.B.Position)>0&&wound.Distance(i,source.C.Position)>0){output.Add(source);return false;}
            int start=output.Count;var rest=new List<Vertex>{source.A,source.B,source.C};
            for(int i=0;i<wound.Normals.Length&&rest.Count>=3;i++){Fan(Clip(rest,wound,i,false),output);rest=Clip(rest,wound,i,true);}
            float area=0;for(int i=1;i+1<rest.Count;i++)area+=Vector3.Cross(rest[i].Position-rest[0].Position,rest[i+1].Position-rest[0].Position).magnitude;
            if(area<=1e-9f){for(int i=output.Count-1;i>=start;i--)output.RemoveAt(i);output.Add(source);return false;}
            if(boundary!=null)for(int i=0;i<rest.Count;i++)Add(rest[i],rest[(i+1)%rest.Count],wound,boundary);
            return true;
        }
        private static void Add(Vertex a,Vertex b,Wound w,List<Edge> edges)
        {
            if(!w.Boundary(a.Position)||!w.Boundary(b.Position)||(a.Position-b.Position).sqrMagnitude<=1e-12f)return;
            // Both endpoints must share a cutting plane. Otherwise a fan diagonal
            // between two wound corners would incorrectly receive a folded lip.
            for(int i=0;i<w.RadialPlanes;i++)if(Math.Abs(w.Distance(i,a.Position))<2e-5f&&Math.Abs(w.Distance(i,b.Position))<2e-5f){edges.Add(new Edge(a,b));return;}
        }
    }
}
