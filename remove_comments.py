import os
import re
import sys

# Directories to exclude
EXCLUDE_DIRS = {'.git', '.vs', '.vscode', 'bin', 'obj', 'artifacts', 'installers', 'release', '.agent', '.gemini'}
# Extensions to process
INCLUDE_EXTS = {'.cs', '.xaml', '.xml', '.csproj', '.iss', '.md', '.ps1', '.js', '.manifest'}

def remove_comments_from_text(text, ext):
    if ext in ['.cs', '.js', '.ps1', '.manifest']:
        # C-style comments: // and /* */
        # Regex explanation:
        # Group 1: Match double quoted strings including escaped characters
        # Group 2: Match single quoted strings including escaped characters
        # Group 3: Match multi-line comments /* ... */
        # Group 4: Match single-line comments // ...
        pattern = r'("(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\')|(/\*[\s\S]*?\*/|//.*)'
        
        def replacer(match):
            if match.group(1):
                return match.group(1) # Keep string content
            return "" # Remove comment content
            
        return re.sub(pattern, replacer, text)
        
    elif ext in ['.xaml', '.xml', '.csproj', '.md']:
        # XML-style comments: <!-- ... -->
        return re.sub(r'<!--[\s\S]*?-->', '', text)
        
    elif ext == '.iss':
        # Inno Setup comments: ; and sometimes // in [Code]
        # First handle // in code parts if possible, but let's stick to ; for now
        # Strings in .iss are usually "..."
        pattern = r'("(?:\\.|[^"\\])*")|(;.*|//.*)'
        def replacer(match):
            if match.group(1):
                return match.group(1)
            return ""
        return re.sub(pattern, replacer, text)
    
    return text

def process_file(filepath):
    ext = os.path.splitext(filepath)[1].lower()
    if ext not in INCLUDE_EXTS:
        return

    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            content = f.read()
            
        new_content = remove_comments_from_text(content, ext)
        
        if content != new_content:
            with open(filepath, 'w', encoding='utf-8') as f:
                f.write(new_content)
            print(f"Processed: {filepath}")
    except Exception as e:
        print(f"Error processing {filepath}: {e}")

def main(root_dir):
    for root, dirs, files in os.walk(root_dir):
        # Filter out excluded directories
        dirs[:] = [d for d in dirs if d not in EXCLUDE_DIRS]
        
        for file in files:
            filepath = os.path.join(root, file)
            process_file(filepath)

if __name__ == "__main__":
    target_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    main(target_dir)
