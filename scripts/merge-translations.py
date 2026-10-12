#!/usr/bin/env python3
import json
from pathlib import Path

root = Path(__file__).resolve().parent.parent
resources = root / 'shared' / 'resources'
merged = {}
counts = {}
for name in ('studio.zh-CN.json', 'desktop.zh-CN.json', 'supplemental.zh-CN.json'):
    source = json.loads((resources / name).read_text(encoding='utf-8'))
    if not isinstance(source, dict) or any(not isinstance(k, str) or not isinstance(v, str) for k, v in source.items()):
        raise ValueError(f'{name} must be an English-to-Chinese string dictionary')
    counts[name] = len(source)
    # Match the runtime's whitespace normalization; leading/trailing spacing stays on DOM nodes.
    merged.update({' '.join(key.split()): value for key, value in source.items()})
for name in ('Untitled', 'HyperFrames', 'Framey', 'Claude', 'Codex', 'GPT', 'Gemini', 'Remotion'):
    merged.pop(name, None)
(resources / 'translations.zh-CN.json').write_text(
    json.dumps(merged, ensure_ascii=False, indent=2, sort_keys=True) + '\n', encoding='utf-8'
)
(root / 'audit' / 'dictionary-counts.json').write_text(
    json.dumps({'sources': counts, 'mergedEntries': len(merged)}, ensure_ascii=False, indent=2) + '\n', encoding='utf-8'
)
print(json.dumps({'sources': counts, 'mergedEntries': len(merged)}, ensure_ascii=False))

report = json.loads((root / 'audit' / 'studio-dictionary-report.json').read_text(encoding='utf-8'))
templates = []
for entry in report.get('dynamic_template_suggestions', []):
    english = entry.get('source_template', '')
    chinese = entry.get('suggested_zh_CN_template', '')
    if english and chinese:
        templates.append({'english': english, 'chinese': chinese})
templates += [
    {'english': 'Renders (${count})', 'chinese': '渲染 (${count})'},
    {'english': 'Import projects (${count})', 'chinese': '导入项目 (${count})'},
    {'english': 'Import these ${count} projects', 'chinese': '导入这 ${count} 个项目'},
    {'english': 'Model: ${model}', 'chinese': '模型：${model}', 'attributesOnly': True},
    {'english': 'Harness: ${agent}', 'chinese': '运行框架：${agent}', 'attributesOnly': True},
    {'english': 'Effort: ${effort}', 'chinese': '推理强度：${effort}', 'attributesOnly': True},
    {'english': 'Agent: ${agent}', 'chinese': '智能体：${agent}', 'attributesOnly': True},
    {'english': 'Preview ${name}', 'chinese': '预览 ${name}', 'attributesOnly': True},
    {'english': 'Play ${name}', 'chinese': '播放 ${name}', 'attributesOnly': True},
    {'english': 'Convert ${name}', 'chinese': '转换 ${name}', 'attributesOnly': True},
    {'english': 'Rename project: ${name}', 'chinese': '重命名项目：${name}', 'attributesOnly': True},
    {'english': '${agent} is connected', 'chinese': '${agent} 已连接'},
    {'english': 'Connect ${agent}', 'chinese': '连接 ${agent}'},
    {'english': 'Installing ${agent}', 'chinese': '正在安装 ${agent}'},
    {'english': 'Updating ${agent}', 'chinese': '正在更新 ${agent}'},
    {'english': 'Framey makes your videos with ${agent} on this computer. Set it up once.', 'chinese': 'Framey 会在这台电脑上使用 ${agent} 制作视频。只需设置一次。'},
    {'english': "System follows your ${computer}'s light or dark setting.", 'chinese': '跟随这台 ${computer} 的浅色或深色设置。'},
    {'english': 'Framey shows what it is doing and tells you when a video is ready or needs you. Off, ${notice} does.', 'chinese': 'Framey 会显示工作状态，并在视频就绪或需要你操作时通知你。关闭后由 ${notice} 通知。'},
    {'english': 'Downloading the video… ${received} of ${total}', 'chinese': '正在下载视频… ${received} / ${total}'},
    {'english': 'Getting the transcription model, ${received} of ${total} MB', 'chinese': '正在获取转写模型，${received} / ${total} MB'},
    {'english': 'Preparing transcript · ${percent}%', 'chinese': '正在准备转写 · ${percent}%'},
    {'english': 'Switch to ${theme} theme', 'chinese': '切换到 ${theme} 主题'},
]
(resources / 'dynamic.zh-CN.json').write_text(
    json.dumps(templates, ensure_ascii=False, indent=2) + '\n', encoding='utf-8'
)
print(json.dumps({'dynamicTemplates': len(templates)}))
