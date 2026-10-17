"""Convert docs/assets/Tux.svg into checked-in, named WPF vector art.

This intentionally handles the SVG constructs used by this file only. Wing
silhouettes/glare and the right tip base/glare are replaced by the WPF rig.
"""

import html
import re
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SVG = ROOT / "docs/assets/Tux.svg"
OUT = ROOT / "src/Igloo.Mascot.Wpf/Themes/Generic.xaml"
ns = "{http://www.w3.org/2000/svg}"
root = ET.parse(SVG).getroot()
defs = root.find(f"{ns}defs")
by_id = {e.get("id"): e for e in root.iter() if e.get("id")}
esc = lambda x: html.escape(str(x), quote=True)


def stops(node):
    href = node.get("href") or node.get("{http://www.w3.org/1999/xlink}href")
    source = by_id[href[1:]] if href else node
    result = []
    for s in source:
        if s.tag != f"{ns}stop":
            continue
        colour = s.get("stop-color")
        alpha = round(float(s.get("stop-opacity", "1")) * 255)
        if alpha < 255:
            colour = f"#{alpha:02x}" + colour[1:]
        result.append(f'<GradientStop Offset="{s.get("offset")}" Color="{colour}" />')
    return result


def brush(node):
    kind = node.tag.removeprefix(ns)
    name = node.get("id")
    if kind == "linearGradient":
        a = f'<LinearGradientBrush x:Key="{name}" MappingMode="Absolute" StartPoint="{node.get("x1")},{node.get("y1")}" EndPoint="{node.get("x2")},{node.get("y2")}">'
        return a + "".join(stops(node)) + "</LinearGradientBrush>"
    transform = node.get("gradientTransform", "")
    translate = re.search(r"translate\(([-\d.]+)[, ]+([-\d.]+)\)", transform)
    rotate = re.search(r"rotate\(([-\d.]+)\)", transform)
    scale = re.search(r"scale\(([-\d.]+)[, ]+([-\d.]+)\)", transform)
    x, y = translate.groups() if translate else ("0", "0")
    angle = rotate.group(1) if rotate else "0"
    sx, sy = scale.groups() if scale else ("1", "1")
    a = f'<RadialGradientBrush x:Key="{name}" MappingMode="Absolute" Center="0,0" GradientOrigin="0,0" RadiusX="1" RadiusY="1">'
    a += "<RadialGradientBrush.Transform><TransformGroup>"
    a += f'<ScaleTransform ScaleX="{sx}" ScaleY="{sy}" /><RotateTransform Angle="{angle}" /><TranslateTransform X="{x}" Y="{y}" />'
    a += "</TransformGroup></RadialGradientBrush.Transform>"
    return a + "".join(stops(node)) + "</RadialGradientBrush>"


clips = {}
for e in defs:
    if e.tag != f"{ns}clipPath":
        continue
    figures = []
    for child in e:
        if child.tag == f"{ns}path":
            figures.append(child.get("d"))
        else:
            href = child.get("href") or child.get("{http://www.w3.org/1999/xlink}href")
            figures.append(by_id[href[1:]].get("d"))
    clips[e.get("id")] = " ".join(figures)
blurs = {}
for e in defs:
    if e.tag == f"{ns}filter":
        child = next(iter(e), None)
        if child is not None and child.tag == f"{ns}feGaussianBlur":
            blurs[e.get("id")] = max(map(float, child.get("stdDeviation").split())) * 2


def reference(value):
    return value.removeprefix("url(#").removesuffix(")") if value else None


def path(node):
    ident = node.get("id")
    fill = node.get("fill", "#020204")
    fill = f'{{StaticResource {reference(fill)}}}' if fill.startswith("url(") else fill
    opacity = float(node.get("opacity", "1")) * float(node.get("fill-opacity", "1"))
    attrs = [f'x:Name="{ident}"', f'Data="{esc(node.get("d"))}"', f'Fill="{esc(fill)}"']
    if opacity != 1:
        attrs.append(f'Opacity="{opacity:.6g}"')
    if ident in {"wing_left_base", "wing_left_glare", "wing_right_base", "wing_right_glare", "wing_tip_right_base", "wing_tip_right_glare_1", "wing_tip_right_glare_2"}:
        attrs.append('Visibility="Collapsed"')
    clip = reference(node.get("clip-path"))
    if clip and ident not in {"head_glare", "eyelid_left", "eyelid_right"}:
        attrs.append(f'Clip="{esc(clips[clip])}"')
    tag = '<Path ' + " ".join(attrs)
    filt = reference(node.get("filter"))
    if filt and ident not in {"wing_left_glare", "wing_right_glare"}:
        return tag + f'><Path.Effect><BlurEffect Radius="{blurs[filt]:.5g}" /></Path.Effect><Path.CacheMode><BitmapCache /></Path.CacheMode></Path>'
    return tag + ' />'


def render(node):
    kind = node.tag.removeprefix(ns)
    if kind == "path":
        body = path(node)
        if node.get("id") in {"head_glare", "eyelid_left", "eyelid_right"}:
            clip = reference(node.get("clip-path"))
            return f'<Canvas x:Name="{node.get("id")}_clip" Width="216" Height="256" Clip="{esc(clips[clip])}">{body}</Canvas>'
        return body
    if kind == "g":
        ident = node.get("id")
        body = "".join(render(c) for c in node)
        return f'<Canvas x:Name="{ident}" Width="216" Height="256">{body}</Canvas>'
    if kind == "use":
        href = node.get("href") or node.get("{http://www.w3.org/1999/xlink}href")
        source = by_id[href[1:]]
        clone = ET.Element(f"{ns}path", node.attrib)
        clone.set("d", source.get("d"))
        return path(clone)
    return ""


resources = "".join(brush(e) for e in defs if e.tag in {f"{ns}linearGradient", f"{ns}radialGradient"} and e.get("id", "").startswith("fill_"))
art = render(next(e for e in root if e.get("id") == "tux"))
xaml = '''<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:Igloo.Mascot.Wpf">
  <ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="/Igloo.Mascot.Wpf;component/Themes/TaskCardRing.xaml" />
  </ResourceDictionary.MergedDictionaries>
  <Style TargetType="{x:Type local:TuxAvatar}">
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="{x:Type local:TuxAvatar}">
          <Viewbox x:Name="PART_Viewbox" Stretch="Uniform">
            <Canvas x:Name="PART_Root" Width="216" Height="256">
              <Canvas.Resources>''' + resources + '''</Canvas.Resources>''' + art + '''
            </Canvas>
          </Viewbox>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
'''
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(xaml, encoding="utf-8")
names = re.findall(r'x:Name="([^"]+)"', xaml)
required = {"tux", "wing_left", "wing_left_base", "wing_left_glare", "wing_right",
            "wing_right_base", "wing_right_glare", "wing_tip_right", "wing_tip_right_base",
            "wing_tip_right_shadow", "wing_tip_right_glare", "wing_tip_right_glare_1",
            "wing_tip_right_glare_2", "face", "pupil_left", "pupil_right", "eyelid_left",
            "eyelid_right", "eyebrow_left", "eyebrow_right", "mandible_upper",
            "mandible_lower", "head_glare", "body_base", "feet"}
assert required.issubset(names), sorted(required - set(names))
assert len(names) == len(set(names)), "duplicate XAML names"
print(f"{OUT}: {len(names)} preserved names; {len(clips)} clips; {len(blurs)} blur definitions")
