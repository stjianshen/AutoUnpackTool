#!/usr/bin/env python3
"""
智能分析 Git 变更并按功能分组

输出格式化的变更分析报告，帮助确定如何拆分 commit
"""

import subprocess
import sys
import re
from collections import defaultdict
from typing import Dict, List, Tuple


def run_git_command(cmd: str) -> str:
    """执行 git 命令并返回输出"""
    try:
        result = subprocess.run(
            cmd.split(),
            capture_output=True,
            text=True,
            check=True,
            encoding='utf-8'
        )
        return result.stdout
    except subprocess.CalledProcessError as e:
        print(f"错误: git 命令执行失败 - {e.stderr}", file=sys.stderr)
        sys.exit(1)


def get_changed_files() -> List[Tuple[str, str]]:
    """获取所有修改的文件及其状态"""
    output = run_git_command("git status --porcelain")
    files = []
    for line in output.strip().split('\n'):
        if line:
            status = line[:2].strip()
            filepath = line[3:]
            files.append((status, filepath))
    return files


def get_file_diff_stats(filepath: str) -> Dict[str, int]:
    """获取文件的变更统计"""
    output = run_git_command(f"git diff HEAD -- {filepath}")
    additions = output.count('\n+') - output.count('\n+++')
    deletions = output.count('\n-') - output.count('\n---')
    return {'additions': additions, 'deletions': deletions}


def classify_change_type(filepath: str, diff_content: str) -> str:
    """
    根据文件路径和变更内容判断变更类型
    
    返回: feat, fix, refactor, docs, style, test, chore
    """
    # 基于文件路径的初步判断
    if filepath.endswith(('.md', '.txt', 'README')):
        return 'docs'
    
    if 'test' in filepath.lower() or filepath.endswith('.test.cs'):
        return 'test'
    
    if filepath.endswith(('.bat', '.sh', '.yml', '.yaml', '.json')):
        return 'chore'
    
    # 基于变更内容的判断
    diff_lower = diff_content.lower()
    
    # 关键词检测
    fix_keywords = ['fix', 'bug', '修复', '错误', 'problem', 'issue', 'crash', 'fail']
    feat_keywords = ['feat', 'add', 'new', '新增', '功能', 'feature', 'implement']
    refactor_keywords = ['refactor', '重构', 'optimize', '优化', 'rename', 'move', 'extract']
    style_keywords = ['format', '格式化', 'indent', 'whitespace', 'lint']
    
    for keyword in fix_keywords:
        if keyword in diff_lower:
            return 'fix'
    
    for keyword in feat_keywords:
        if keyword in diff_lower:
            return 'feat'
    
    for keyword in refactor_keywords:
        if keyword in diff_lower:
            return 'refactor'
    
    for keyword in style_keywords:
        if keyword in diff_lower:
            return 'style'
    
    # 默认根据文件类型判断
    if filepath.endswith('.cs'):
        return 'refactor'  # C# 代码变更默认为重构
    
    return 'chore'


def extract_scope(filepath: str) -> str:
    """从文件路径提取模块范围"""
    # 常见模块映射
    scope_map = {
        'MainWindow': 'ui',
        'SettingsDialog': 'ui',
        'PasswordManager': 'password',
        'SevenZipExtractor': 'core',
        'AppSettings': 'config',
        'App.xaml': 'ui',
    }
    
    filename = filepath.split('/')[-1].split('\\')[-1]
    base_name = filename.replace('.xaml.cs', '').replace('.xaml', '').replace('.cs', '')
    
    for key, scope in scope_map.items():
        if key.lower() in base_name.lower():
            return scope
    
    # 根据目录判断
    if 'Dialog' in filepath:
        return 'ui'
    if 'Extractor' in filepath or 'Unpack' in filepath:
        return 'core'
    
    return 'general'


def generate_commit_message(change_type: str, scope: str, files: List[str], stats: Dict) -> str:
    """生成 commit 消息"""
    # 根据变更类型和文件生成描述
    descriptions = {
        'fix': {
            'ui': '修复界面相关问题',
            'core': '修复核心功能问题',
            'password': '修复密码管理相关问题',
            'config': '修复配置相关问题',
            'general': '修复已知问题',
        },
        'feat': {
            'ui': '添加新的界面功能',
            'core': '添加核心功能',
            'password': '增强密码管理功能',
            'config': '添加新的配置选项',
            'general': '添加新功能',
        },
        'refactor': {
            'ui': '重构界面代码结构',
            'core': '重构核心逻辑',
            'password': '优化密码管理代码',
            'config': '重构配置管理',
            'general': '代码重构和优化',
        },
        'docs': {
            'general': '更新文档',
        },
        'test': {
            'general': '添加或更新测试',
        },
        'style': {
            'general': '代码格式化',
        },
        'chore': {
            'general': '更新构建配置或依赖',
        },
    }
    
    description = descriptions.get(change_type, {}).get(scope, '一般性变更')
    
    # 如果有多个文件，添加简要说明
    if len(files) > 1:
        file_names = [f.split('/')[-1].split('\\')[-1] for f in files[:3]]
        description += f" ({', '.join(file_names)}{'...' if len(files) > 3 else ''})"
    
    return f"{change_type}({scope}): {description}"


def analyze_changes():
    """主分析函数"""
    print("=" * 70)
    print("Git 变更分析报告")
    print("=" * 70)
    print()
    
    # 获取变更文件
    changed_files = get_changed_files()
    
    if not changed_files:
        print("没有检测到未提交的变更")
        return
    
    print(f"检测到 {len(changed_files)} 个文件变更:\n")
    
    # 按变更类型分组
    groups = defaultdict(lambda: {'files': [], 'stats': {'additions': 0, 'deletions': 0}})
    
    for status, filepath in changed_files:
        # 跳过删除的文件（单独处理）
        if 'D' in status:
            continue
        
        # 获取变更统计
        stats = get_file_diff_stats(filepath)
        
        # 获取 diff 内容用于分类
        diff_output = run_git_command(f"git diff HEAD -- {filepath}")
        
        # 分类
        change_type = classify_change_type(filepath, diff_output)
        scope = extract_scope(filepath)
        group_key = f"{change_type}|{scope}"
        
        groups[group_key]['files'].append(filepath)
        groups[group_key]['stats']['additions'] += stats['additions']
        groups[group_key]['stats']['deletions'] += stats['deletions']
        groups[group_key]['type'] = change_type
        groups[group_key]['scope'] = scope
    
    # 输出分组结果
    print(f"\n建议拆分为 {len(groups)} 个 commit:\n")
    print("-" * 70)
    
    for idx, (group_key, data) in enumerate(groups.items(), 1):
        change_type = data['type']
        scope = data['scope']
        files = data['files']
        stats = data['stats']
        
        commit_msg = generate_commit_message(change_type, scope, files, stats)
        
        print(f"\n【分组 {idx}】{commit_msg}")
        print(f"  文件 ({len(files)} 个):")
        for f in files:
            print(f"    - {f}")
        print(f"  变更: +{stats['additions']} -{stats['deletions']} 行")
    
    print("\n" + "-" * 70)
    print("\n提示: 审查以上分组，确认是否合理。")
    print("如需调整，可以手动指定文件分组或使用 git add -p 交互式选择。")
    print()


if __name__ == '__main__':
    analyze_changes()
