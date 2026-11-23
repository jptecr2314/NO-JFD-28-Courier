using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyCargoIcons
    {
        private static readonly Dictionary<UnitDefinition,Sprite> Icons=new Dictionary<UnitDefinition,Sprite>();
        private static readonly Dictionary<WeaponMount,WeaponInfo> Infos=new Dictionary<WeaponMount,WeaponInfo>();
        private const int Width=512,Height=256,Layer=31;
        internal static void Populate(WeaponManager manager)
        {
            foreach(var mount in manager.hardpointSets.Where(s=>s?.weaponOptions!=null)
                .SelectMany(s=>s.weaponOptions).Where(m=>m!=null&&m.Cargo&&m.prefab!=null&&m.info!=null).Distinct())
            {
                if(Infos.ContainsKey(mount))continue;
                var cargo=mount.prefab.GetComponentInChildren<MountedCargo>(true);
                if(cargo?.cargo==null)continue;
                if(!Icons.TryGetValue(cargo.cargo,out var icon))
                {
                    try{icon=Render(mount.prefab,cargo.cargo);}catch(Exception error)
                    {Debug.LogWarning("Courier cargo profile "+cargo.cargo.jsonKey+": "+error.Message);}
                    if(icon==null)continue;
                    Icons[cargo.cargo]=icon;
                }
                // Avoid changing a weapon info shared by unrelated vehicles.
                var previous=mount.info;
                var info=Object.Instantiate(previous);info.name=previous.name+"_CourierProfile_"+cargo.cargo.jsonKey;
                info.weaponIcon=icon;info.description=cargo.cargo.description;
                foreach(var item in mount.prefab.GetComponentsInChildren<MountedCargo>(true))
                    if(item.info==previous)item.info=info;
                mount.info=info;Infos[mount]=info;
            }
        }
        private static Sprite Render(GameObject source,UnitDefinition definition)
        {
            var shader=Shader.Find("Universal Render Pipeline/Unlit")??Shader.Find("Unlit/Color");
            if(shader==null)return null;
            var host=new GameObject("Courier cargo profile capture");host.transform.position=new Vector3(90000,90000,90000);
            var materials=new List<Material>();
            var cameraObject=new GameObject("Courier cargo profile camera");var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;
            var target=new RenderTexture(Width,Height,24,RenderTextureFormat.ARGB32);
            var texture=new Texture2D(Width,Height,TextureFormat.RGBA32,false);
            var previous=RenderTexture.active;
            bool success=false;
            try
            {
                var low=new HashSet<Renderer>();var primary=new HashSet<Renderer>();
                foreach(var group in source.GetComponentsInChildren<LODGroup>(true))
                {var lods=group.GetLODs();if(lods.Length==0)continue;primary.UnionWith(lods[0].renderers);foreach(var lod in lods.Skip(1))low.UnionWith(lod.renderers);}
                low.ExceptWith(primary);
                Bounds bounds=default;bool any=false;int number=0;
                foreach(var original in source.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if(low.Contains(original)||!original.enabled||!SuperheavyQolStores.IsVisibleInTemplate(original.transform,source.transform)||
                        !original.TryGetComponent<MeshFilter>(out var filter)||filter.sharedMesh==null)continue;
                    var node=new GameObject(original.name);node.layer=Layer;node.transform.SetParent(host.transform,false);
                    node.transform.localPosition=source.transform.InverseTransformPoint(original.transform.position);
                    node.transform.localRotation=Quaternion.Inverse(source.transform.rotation)*original.transform.rotation;
                    var scale=original.transform.lossyScale;var rootScale=source.transform.lossyScale;
                    node.transform.localScale=new Vector3(scale.x/rootScale.x,scale.y/rootScale.y,scale.z/rootScale.z);
                    node.AddComponent<MeshFilter>().sharedMesh=filter.sharedMesh;
                    var material=new Material(shader);float shade=.35f+(number++%8)*.075f;
                    material.SetColor("_BaseColor",new Color(shade,shade,shade,1));material.color=new Color(shade,shade,shade,1);materials.Add(material);
                    var renderer=node.AddComponent<MeshRenderer>();renderer.sharedMaterials=Enumerable.Repeat(material,filter.sharedMesh.subMeshCount).ToArray();
                    renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
                    if(!any){bounds=renderer.bounds;any=true;}else bounds.Encapsulate(renderer.bounds);
                }
                if(!any)return null;
                camera.cullingMask=1<<Layer;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                camera.orthographic=true;camera.orthographicSize=Mathf.Max(bounds.size.y*.7f,bounds.size.z*.35f);
                camera.aspect=2f;camera.nearClipPlane=.1f;camera.farClipPlane=200f;camera.allowHDR=false;camera.allowMSAA=false;
                camera.transform.position=bounds.center+Vector3.left*50f;
                camera.transform.rotation=Quaternion.LookRotation(Vector3.right,Vector3.up);camera.targetTexture=target;
                target.Create();
                if(GraphicsSettings.currentRenderPipeline==null)camera.Render();
                else RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest{destination=target});
                RenderTexture.active=target;texture.ReadPixels(new Rect(0,0,Width,Height),0,0);texture.Apply();
                var pixels=texture.GetPixels32();var ink=new Color32[pixels.Length];int marked=0;
                for(int y=1;y<Height-1;y++)for(int x=1;x<Width-1;x++)
                {
                    int i=y*Width+x;int value=pixels[i].r;byte line=0;
                    if(value>20)
                    {
                        foreach(int n in new[]{i-1,i+1,i-Width,i+Width})
                            line=(byte)Math.Max(line,pixels[n].r<=20?255:Math.Abs(value-pixels[n].r)>9?190:0);
                    }
                    ink[i]=new Color32(line,line,line,255);if(line>0)marked++;
                }
                if(marked<40)return null;
                var softened=(Color32[])ink.Clone();
                for(int y=1;y<Height-1;y++)for(int x=1;x<Width-1;x++)
                {
                    int i=y*Width+x;
                    byte value=(byte)Math.Max(ink[i].r,Math.Max(Math.Max(ink[i-1].r,ink[i+1].r),Math.Max(ink[i-Width].r,ink[i+Width].r))*3/5);
                    softened[i]=new Color32(value,value,value,255);
                }
                ink=softened;
                texture.SetPixels32(ink);texture.Apply();texture.name="CourierProfile_"+definition.jsonKey;
                texture.filterMode=FilterMode.Bilinear;texture.wrapMode=TextureWrapMode.Clamp;
                var sprite=Sprite.Create(texture,new Rect(0,0,Width,Height),new Vector2(.5f,.5f),100f,0,SpriteMeshType.FullRect);
                sprite.name=texture.name;success=true;return sprite;
            }
            finally
            {
                RenderTexture.active=previous;camera.targetTexture=null;host.SetActive(false);target.Release();Dispose(target);
                Dispose(host);Dispose(cameraObject);foreach(var material in materials)Dispose(material);
                if(!success)Dispose(texture);
            }
        }
        private static void Dispose(Object value)
        {if(Application.isPlaying)Object.Destroy(value);else Object.DestroyImmediate(value);}
    }
}
