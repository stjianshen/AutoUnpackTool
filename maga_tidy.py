# coding:utf-8
import hashlib
import logging
import os
import random
import re
import shutil
import time
import uuid
from concurrent.futures import ThreadPoolExecutor

from send2trash import send2trash

from lcs_max import lcs_max_char

# 配置日志
logging.basicConfig(
    level=logging.INFO,
    format='%(funcName)s -  %(lineno)s - %(levelname)s - %(message)s',
    handlers=[
        logging.FileHandler("maga_tiday.log", encoding="utf-8"),
        logging.StreamHandler()
    ]
)

# 创建线程池
pool = ThreadPoolExecutor(max_workers=8)

def log_decorator(func):
    def wrapper(*args, **kwargs):
        logging.info(f"开始执行函数: {func.__name__}")
        result = func(*args, **kwargs)
        logging.info(f"结束执行函数: {func.__name__}")
        return result
    return wrapper


def clean(path):
    rubbish = []
    ignore_list = []
    with open('./ignore.txt', 'r', encoding="utf-8") as fi:
        while True:
            line = fi.readline()
            if not line:
                break
            line = line.lower().strip()
            ignore_list.append(line)
    # logging.info('load ignore list {}'.format(ignore_list))
    with os.scandir(path) as it:
        for entry in it:
            if entry.is_file():
                item = entry.name.lower()
                if item in ignore_list or item.endswith(("url", "html", 'ini', "torrent", "htm", 'db', 'mht', 'doc', 'docx', 'tmp')):
                    send2trash(entry.path)
                    rubbish.append(item)
            elif entry.is_dir():
                if not test_and_rm_empty_dir(entry.path):
                    clean(entry.path)
            else:
                logging.error(f"find unusual file {entry.path}")
    if rubbish:
        logging.info(f"删除垃圾文件 {len(rubbish)} 个\n{rubbish}")

def test_and_rm_empty_dir(path):
    if not os.listdir(path):
        send2trash(path)
        logging.info(f'删除空文件夹{path}')
        return True
    return False


def scan_folders(source_dir, pattern=None, sort=False, abs_path=True):
    """
    扫描源目录中符合给定模式的文件夹。

    参数:
        source_dir (str): 源目录的路径。
        pattern (str, optional): 用于匹配文件夹名称的正则表达式模式。默认为 None。
        if_sort (bool, optional): 是否对匹配的文件夹进行排序。默认为 False。

    返回:
        list: 匹配文件夹路径的列表。
    """
    matched_folders = []
    complied_pattern = None
    if pattern:
        complied_pattern = re.compile(pattern, re.IGNORECASE)
    # 遍历源目录
    for item in os.listdir(source_dir):
        item_path = os.path.join(source_dir, item).replace('\\', '/')
        if abs_path:
            r_item = item_path
        else:
            r_item = os.path.basename(item)
            

        # 只处理文件夹
        if os.path.isdir(item_path):
            if complied_pattern:
                # 检查文件夹名是否匹配模式
                if complied_pattern.match(item):
                    matched_folders.append(r_item)
            else:
                matched_folders.append(r_item)
    if sort:
        matched_folders.sort()
    return matched_folders


def move_folder(source_path, target_dir, move_inside=True):
    """
    移动或重命名文件夹

    Args:
        source_path: 源文件夹路径
        target_dir: 目标目录路径
        move_inside: 是否移动到标目录内部,True为移动,False为重命名为目标路径
    """
    source_path = source_path.replace('\\', '/')
    target_dir = target_dir.replace('\\', '/')
    # 确保目标目录存在
    if move_inside:
        if not os.path.exists(target_dir):
            os.makedirs(target_dir)

        folder_name = os.path.basename(source_path)
        target_path = os.path.join(target_dir, folder_name)

        # 如果目标路径已存在，添加数字后缀
        if os.path.exists(target_path):
            counter = 1
            while os.path.exists(f"{target_path}_{counter}"):
                counter += 1
            target_path = f"{target_path}_{counter}"
    else:
        target_path = target_dir

    # 移动文件夹
    try:
        if move_inside:
            if os.path.exists(target_path) and os.path.isdir(target_path):
                temp_path = os.path.join(target_dir, time.time())
                shutil.move(source_path, temp_path)
                os.rmdir(os.path.dirname(source_path))
                shutil.move(temp_path, target_path)
            else:
                shutil.move(source_path, target_path)
        else:
            if os.path.exists(target_path) and os.path.isdir(target_path):
                move_folder_content(source_path, target_path)
                os.rmdir(source_path)
            else:
                os.rename(source_path, target_path)
        logging.info("已%s: %s -> %s", '移动' if move_inside else '重命名', os.path.basename(source_path), target_path)
    except Exception as e:
        logging.error("%s失败 %s: %s", '移动' if move_inside else '重命名', os.path.basename(source_path), str(e))


@log_decorator
def rename_folders(directory):
    """
    扫描目录并重命名符合特定格式的文件夹
    将汉化组名从文件夹名开头移到末尾

    Args:
        directory: 要扫描的目录路径
        pool: 线程池对象
    """
    pattern = r'^(?P<scanlator>\[\w*?(?:汉化|漢化|文|扫图|天鹅之恋|CE家族社|脸肿|空气系|魔皮卡|無邪気|翻訳|翻译|CE|Digital|工房|天鹅之恋)\w*?\])\s*(?P<remaining>.*)$'

    matched_folders = scan_folders(directory, pattern)

    def rename_inner(folder_path):
        folder_name = os.path.basename(folder_path)
        match = re.match(pattern, folder_name)
        if not match:
            return
        scanlator = f'[{match.group("scanlator")}]'
        remaining = match.group("remaining").strip()
        # 构建新文件夹名
        new_name = f"{remaining}{scanlator}"
        new_path = os.path.join(directory, new_name)

        # 移动到新路径(相当于重命名)
        move_folder(folder_path, new_path, move_inside=False)

    # 处理每个匹配的文件夹
    wait_pool_exec(rename_inner, matched_folders)


@log_decorator
def organize_by_author(directory, target_dir):
    """
    扫描目录,将文件夹按作者名分类整理
    将类似"[作者名] 标题..."格式的文件夹移动到以作者名命名的子文件夹中

    Args:
        directory: 要扫描的目录路径
    """
    # 匹配格式: [作者名] 标题...
    name_pattern = re.compile(r'^.*?\[(?P<author>[^\]]+)\].*$')

    # 获取所有文件夹
    matched_folders = scan_folders(directory)

    # 处理每个匹配的文件夹
    for folder_path in matched_folders:
        folder_name = os.path.basename(folder_path)
        match = name_pattern.match(folder_name)

        if match:
            # 提取作者名
            author = match.group("author")
 
            # 在目标目录下创建作者文件夹
            author_dir = os.path.join(target_dir, author)
            if not os.path.exists(author_dir):
                os.makedirs(author_dir)

            move_folder(folder_path, os.path.join(author_dir, folder_name), move_inside=False)

@log_decorator
def move_single_files_folders(source_dir, target_dir):
    """
    扫描目录,将只包含文件(不包含子文件夹)的文件夹移动到目标目录

    Args:
        source_dir: 要扫描的源目录路径
        target_dir: 移动的目标目录路径
    """
    # 确保目标目录存在
    if not os.path.exists(target_dir):
        os.makedirs(target_dir)

    # 遍历源目录中的所有文件夹
    for item in os.listdir(source_dir):
        folder_path = os.path.join(source_dir, item)

        # 检查是否是文件夹
        if os.path.isdir(folder_path):
            # 检查文件夹内是否只包含文件
            contains_only_files = True
            for sub_item in os.listdir(folder_path):
                if os.path.isdir(os.path.join(folder_path, sub_item)):
                    contains_only_files = False
                    break
                else:
                    # 检查是否为图片文件
                    file_path = os.path.join(folder_path, sub_item)
                    file_ext = os.path.splitext(file_path)[1].lower()
                    if file_ext not in ['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp']:
                        contains_only_files = False
                        break

            # 如果文件夹只包含文件,则移动到目标目录
            if contains_only_files:
                move_folder(folder_path, target_dir)

def wait_pool_exec(fn, args):
    
    futures = [pool.submit(fn, arg) for arg in args]
    for future in futures:
        future.result()


@log_decorator
def move_single_subfolder(source_dir, use_subdir=False):
    """
    扫描目录,将只包含一个子文件夹的文件夹进行处理：

    Args:
        source_dir: 要扫描的源目录路径
        target_dir: 移动的目标目录路径（可选）
    """
    def inner(folder):
        folder_path = os.path.join(source_dir, folder)
        contents = os.listdir(folder_path)

        if len(contents) == 1:
            
            subfolder = contents[0]
            subfolder_path = os.path.join(folder_path, subfolder)

            if folder == subfolder or not use_subdir:
                logging.info(f'处理同名子文件夹: {subfolder_path}')
                temp_folder_name = f"{folder}_{str(uuid.uuid4())}"
                temp_path = os.path.join(source_dir, temp_folder_name)
                os.rename(subfolder_path, temp_path)
                test_and_rm_empty_dir(folder_path)
                os.rename(temp_path, folder_path)
            else:
                logging.info(f'处理移动子文件夹: {subfolder_path}')
                move_folder(subfolder_path, source_dir, move_inside=True)
                os.rmdir(folder_path)

            logging.info(f'处理子文件夹: {subfolder_path}')
    folders = scan_folders(source_dir, abs_path=False)

    wait_pool_exec(inner, folders)


@log_decorator
def move_single_archive(source_dir):
    """
    扫描目录,将只包含一个压缩文件的文件夹中的压缩文件移动到父目录

    Args:
        source_dir: 要扫描的源目录路径
    """
    # 遍历源目录中的所有文件夹
    for item in os.listdir(source_dir):
        folder_path = os.path.join(source_dir, item)

        # 检查是否是文件夹
        if os.path.isdir(folder_path):
            # 获取文件夹内的所有内容
            contents = os.listdir(folder_path)

            # 如果有一个文件
            if len(contents) == 1:
                file_path = os.path.join(folder_path, contents[0])
                # 检查是否是压缩文件
                if os.path.isfile(file_path) and file_path.lower().endswith(('.zip', '.rar', '.7z')):
                    # 将压缩文件移动到父目录
                    if os.path.exists(os.path.join(source_dir, os.path.basename(file_path))):
                        send2trash(file_path)
                    else:
                        shutil.move(file_path, source_dir)
                    # 删除现在为空的文件夹
                    os.rmdir(folder_path)


@log_decorator
def check_duplicate_folders(source_dir):
    """
    扫描文件夹内的文件夹，检查相邻文件夹是否内容重复
    
    Args:
        source_dir: 要扫描的源目录路径
    """
    # 获取所有子文件夹并排序
    folders = []
    for item in os.listdir(source_dir):
        item_path = os.path.join(source_dir, item)
        if os.path.isdir(item_path):
            folders.append(item)
    folders.sort()
    # 比较相邻文件夹
    # 用字典缓存文件夹大小
    folder_sizes = {}
    
    folders_to_delete = []

    for i in range(len(folders)-1):
        # 使用lcs_max_char计算最长公共子串
        common = lcs_max_char(folders[i], folders[i+1])
        
        # 如果公共子串长度大于4
        if len(common) > 4:

            folder1 = os.path.join(source_dir, folders[i])
            folder2 = os.path.join(source_dir, folders[i+1])
            
            # 获取两个文件夹的内容列表
            files1 = set(os.listdir(folder1))
            files2 = set(os.listdir(folder2))
            
            # 如果内容完全相同
            if files1 == files2:
                # 从缓存获取或计算文件夹大小
                if folder1 not in folder_sizes:
                    folder_sizes[folder1] = sum(os.path.getsize(os.path.join(folder1, f)) for f in files1)
                if folder2 not in folder_sizes:
                    folder_sizes[folder2] = sum(os.path.getsize(os.path.join(folder2, f)) for f in files2)
                
                size1 = folder_sizes[folder1]
                size2 = folder_sizes[folder2]
                
                # 如果大小相同,进一步检查文件内容
                if size1 == size2:
                    # 随机抽取3个文件进行MD5对比
                    files_list = list(files1)  # 因为内容相同,用files1或files2都可以
                    if len(files_list) >= 3:
                        sample_files = random.sample(files_list, 3)
                    else:
                        sample_files = files_list  # 如果文件少于3个就全部检查
                        
                    # 检查所选文件的MD5是否相同
                    files_identical = True
                    for f in sample_files:
                        md5_1 = hashlib.md5(open(os.path.join(folder1, f),'rb').read()).hexdigest()
                        md5_2 = hashlib.md5(open(os.path.join(folder2, f),'rb').read()).hexdigest()
                        if md5_1 != md5_2:
                            files_identical = False
                            break
                            
                    if files_identical:
                        # 记录名称较长的文件夹
                        if len(folders[i]) > len(folders[i+1]):
                            folder_to_delete = folder1
                            folder_name = folders[i]
                        else:
                            folder_to_delete = folder2
                            folder_name = folders[i+1]
                            
                        logging.info(f'发现完全重复的文件夹,将删除: {folder_name}')
                        folders_to_delete.append(folder_to_delete)
    folder_to_delete = list(set(folders_to_delete))
    # 统一删除记录的文件夹
    wait_pool_exec(send2trash, folders_to_delete)

def move_folder_content(source_path, target_path):
    """内容物移动到文件夹"""
    def inner(source_item):
        try:
            shutil.move(source_item, target_path)
        except Exception as e:
            logging.error(f'移动文件失败: {source_item} -> {target_path} - {e}')

    wait_pool_exec(inner, [os.path.join(source_path, item) for item in os.listdir(source_path)])


@log_decorator
def normalize_and_merge_folders(source_dir):
    """
    扫描文件夹内的文件夹，排序，查找相邻文件夹将文件名字的‘（）’换成‘()’。把空格去掉，合并到同一个文件夹

    Args:
        source_dir: 要扫描的源目录路径
    """
    author_pattern = re.compile(r'^(?P<name>.*?)\s*(?:\((?P<extend>.*?)\))?\s*$')
    def normalize_author(author_match):
        author = author_match.group('name').strip()
        if author_match.group('extend'):
            extend = author_match.group('extend').strip()
            author = f'{author}({extend})'
        return author
    
    sufix_pattern = re.compile(r'(?P<name>.*?)\s*(?P<no_use_sufix>(?:_\d)*)$')
    folders = scan_folders(source_dir, abs_path=False)
    for folder in folders:
        folder_path = os.path.join(source_dir, folder)
        # 标准化文件夹名称
        # 将中文括号替换为英文括号
        normalized_folder = re.sub(r'（|）', lambda x: '(' if x.group() == '（' else ')', folder)
        normalized_folder = re.sub(r'『|』', lambda x: '[' if x.group() == '『' else ']', folder)
        normalized_folder = re.sub(r'【|】', lambda x: '[' if x.group() == '【' else ']', folder)
        tongren_pattern = re.compile(r'\(同人[志誌]?\)')
        # 去除空格
        normalized_folder = author_pattern.sub(normalize_author, normalized_folder, count=1)
        # 去除多余的后缀
        normalized_folder = sufix_pattern.sub(lambda match: match.group("name"), normalized_folder, count=1)
        normalized_folder = tongren_pattern.sub('', normalized_folder)
        normalized_folder = re.sub(r'\)\s+\[', ')[', normalized_folder)

        normalized_folder = normalized_folder.strip()
        normalize_folder_path = os.path.join(source_dir, normalized_folder.strip())
        if folder != normalized_folder:
            if not os.path.exists(normalize_folder_path):
                os.rename(folder_path, normalize_folder_path)
                logging.info(f'标准化文件夹: {folder} -> {normalized_folder}')
                continue
            move_folder(folder_path, normalize_folder_path)
            logging.info(f'标准化文件夹1: {folder} -> {normalized_folder}')

def scan_and_move_comilation_folder(source_dir, dest_dir):
    def inner(folder):
        for item in os.listdir(folder):
            move_flag = True
            if not os.path.isdir(os.path.join(folder, item)):
                move_flag = False
                break
        if move_flag:
            move_folder(folder, dest_dir)
    dirs = scan_folders(source_dir, pattern=r'^\[.*\]$')
    wait_pool_exec(inner, dirs)

def check_folder_name_contains(source_dir, target_dir):
    """检查source_dir中的文件夹名称是否包含在target_dir的文件夹名称中
    
    Args:
        source_dir: 源目录路径
        target_dir: 目标目录路径,用于检查的目录
    """
    source_folders = scan_folders(source_dir)
    target_folders = scan_folders(target_dir)
    
    for source_folder in source_folders:
        source_folder_name = os.path.basename(source_folder).strip("[]")
        for target_folder in target_folders:
            target_folder_name = os.path.basename(target_folder)
            if source_folder_name in target_folder_name:
                logging.info(f'找到包含关系: { os.path.basename(source_folder)} -> { os.path.basename(target_folder)}')


def remove_t_jpg(path):
    dirs = scan_folders(path, abs_path=True)
    wait_pool_exec(delete_similar_images, dirs)

def delete_similar_images(folder_path, extensions=None):
    if extensions is None:
        # 默认支持的图片格式
        extensions = ['.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tiff']

    # 获取文件夹下所有文件
    files = os.listdir(folder_path)

    # 只筛选出支持的图片格式文件
    image_files = [file for file in files if any(file.lower().endswith(ext) for ext in extensions)]

    # 用一个字典来记录文件前缀（不包含 t 的）
    seen_files = set()
    if not os.path.exists(folder_path):
        logging.error(f"don not exist :{folder_path}")
        return
    # 遍历所有图片文件
    for file in image_files:
        # 提取文件名的数字部分（去掉扩展名）
        base_name = file.split('.')[0]

        # 如果文件名中包含 "t"，且与已有的前缀相同，则删除该文件
        if 't' in base_name:
            prefix = base_name.rstrip('t')  # 去掉尾部的 't' 以获取前缀
            if prefix in seen_files:
                file_path = os.path.join(folder_path, file)
                try:
                    os.remove(file_path)
                except Exception as e:
                    logging.error(f"fail rm: {file_path}\n{e}")
                print(f"Deleted {file}")
        else:
            # 如果文件名不包含 t，则将它的前缀记录下来
            seen_files.add(base_name)

@log_decorator
def move_unique_items(source_dir, target_dir):
    """
    对比两个文件夹的第一层内容，将源文件夹中独有的项目移动到目标文件夹。

    Args:
        source_dir (str): 源文件夹路径
        target_dir (str): 目标文件夹路径
    """
    # 确保目标目录存在
    if not os.path.exists(target_dir):
        os.makedirs(target_dir)
        
    # 获取两个文件夹的内容
    source_items = set(scan_folders(source_dir, abs_path=False))
    target_items = set(scan_folders(target_dir, abs_path=False))
    
    # 找出源文件夹独有的项目
    unique_items = source_items - target_items
    total = len(unique_items)
    count = 0
    # 移动独有项目
    for item in unique_items:
        count += 1
        logging.info(f"{count}/{total} {item}")
        source_path = os.path.join(source_dir, item)
        logging.info("mv {}".format(item))
        move_folder(source_path=source_path, target_dir=target_dir, move_inside=True)

if __name__ == "__main__":
    # 设置源目录和目标目录
    base_drive = r"D:\写真"
    source_directory = os.path.join(base_drive, "Messie Huang")
    # target_directory = os.path.join(base_drive, "untidy")  # 替换为实际的目标目录
    # compilation_directory = os.path.join(base_drive,  "comilation_template")

    # scan_and_move_comilation_folder(source_directory, target_directory)
    # check_folder_name_contains(r'L:\合集',"H:\合集")


    # source_directory = r'C:\Users\linxi\Downloads\新建文件夹'
    clean(source_directory)
    move_single_archive(source_directory)
    move_single_subfolder(source_directory, use_subdir=False)
    # clean(source_directory)
    # normalize_and_merge_folders(source_directory)
    # rename_folders(source_directory)
    #
    # move_single_files_folders(source_directory, target_directory)
    # check_duplicate_folders(target_directory)
    # check_duplicate_folders(r'H:\合集\[無限軌道A (トモセシュンサク)]')

    # normalize_and_merge_folders(target_directory)
    # organize_by_author(target_directory, compilation_directory)

    # move_unique_items('L:\PL_本篇', 'H:\合集\PL_本篇')