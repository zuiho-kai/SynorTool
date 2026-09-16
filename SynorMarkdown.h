#import <Foundation/Foundation.h>

/// 把浏览器复制出来的 HTML 片段（Chromium/WebKit 的 public.html）转成 Markdown。
///
/// 用途：翻译面板默认用的是辅助功能读到的 AXSelectedText，它会丢掉 Markdown 链接、
/// 行内代码等格式；而应用自己按 ⌘C 写进 public.html 的片段保留了这些结构。
///
/// 只覆盖复制场景常见的标签：段落、标题、列表、代码、引用、链接、表格、图片与换行。
/// 无法识别的标签会被丢弃但保留其中的文字。
///
/// @param html HTML 片段，可为空。
/// @return 转换后的 Markdown；输入为空时返回空字符串。
NSString *SynorMarkdownFromHTML(NSString *html);
