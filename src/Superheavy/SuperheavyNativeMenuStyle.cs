using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Japensou.NuclearOption.Superheavy
{
    // Reuse the game's actual font, sliced control art and selectable states.
    internal static class SuperheavyNativeMenuStyle
    {
        internal static readonly Color Panel=new Color(.025f,.045f,.038f,.97f);
        internal static readonly Color Recess=new Color(.035f,.065f,.052f,1f);
        internal static readonly Color Control=new Color(.11f,.20f,.15f,1f);
        internal static readonly Color Text=new Color(.67f,.94f,.72f,1f);
        internal static readonly Color Selected=new Color(.22f,.48f,.29f,1f);
        internal static void Apply(Transform root,TMP_Dropdown native)
        {
            if(root==null||native==null)return;
            var caption=native.captionText;
            var art=native.targetGraphic as Image;
            foreach(var text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if(caption!=null){text.font=caption.font;text.fontSharedMaterial=caption.fontSharedMaterial;}
            }
            foreach(var selectable in root.GetComponentsInChildren<Selectable>(true))
            {
                var colors=native.colors;
                selectable.colors=colors;selectable.transition=native.transition;
                var image=selectable.targetGraphic as Image;
                if(image!=null&&art!=null&&art.sprite!=null){image.sprite=art.sprite;image.type=Image.Type.Sliced;}
            }
            var panel=root.GetComponent<Image>();
            if(panel!=null)
            {
                panel.color=Panel;
                var border=root.GetComponent<Outline>()??root.gameObject.AddComponent<Outline>();
                border.effectColor=new Color(.45f,.69f,.49f,.65f);border.effectDistance=new Vector2(1f,-1f);border.useGraphicAlpha=true;
            }
        }
    }
}
