#import "SynorMarkdown.h"

/// 解码 HTML 实体（命名实体与十进制/十六进制数字实体）。
static NSString *SynorDecodeEntities(NSString *text) {
    if ([text rangeOfString:@"&"].location == NSNotFound) return text;
    static NSDictionary<NSString *, NSString *> *named;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        named = @{ @"amp": @"&", @"lt": @"<", @"gt": @">", @"quot": @"\"", @"apos": @"'",
                   @"nbsp": @" ", @"hellip": @"…", @"mdash": @"—", @"ndash": @"–",
                   @"lsquo": @"‘", @"rsquo": @"’", @"ldquo": @"“", @"rdquo": @"”",
                   @"middot": @"·", @"times": @"×", @"copy": @"©", @"reg": @"®",
                   @"trade": @"™", @"laquo": @"«", @"raquo": @"»", @"bull": @"•" };
    });
    NSMutableString *output = [NSMutableString stringWithCapacity:text.length];
    NSUInteger index = 0, length = text.length;
    while (index < length) {
        unichar character = [text characterAtIndex:index];
        if (character != '&') {
            [output appendString:[NSString stringWithCharacters:&character length:1]];
            index += 1;
            continue;
        }
        NSUInteger remaining = MIN((NSUInteger)12, length - index);
        NSRange semicolon = [text rangeOfString:@";" options:0 range:NSMakeRange(index, remaining)];
        if (semicolon.location == NSNotFound) {
            [output appendString:@"&"];
            index += 1;
            continue;
        }
        NSString *body = [text substringWithRange:NSMakeRange(index + 1, semicolon.location - index - 1)];
        NSString *decoded = nil;
        if ([body hasPrefix:@"#"]) {
            NSString *digits = [body substringFromIndex:1];
            unsigned int code = 0;
            if ([digits.lowercaseString hasPrefix:@"x"]) {
                [[NSScanner scannerWithString:[digits substringFromIndex:1]] scanHexInt:&code];
            } else {
                int decimal = 0;
                [[NSScanner scannerWithString:digits] scanInt:&decimal];
                if (decimal > 0) code = (unsigned int)decimal;
            }
            // 只处理基本平面，代理对在复制片段里极少出现。
            if (code > 0 && code <= 0xFFFF) { unichar unit = (unichar)code; decoded = [NSString stringWithCharacters:&unit length:1]; }
        } else {
            decoded = named[body.lowercaseString];
        }
        if (decoded) {
            [output appendString:decoded];
            index = semicolon.location + 1;
        } else {
            [output appendString:@"&"];
            index += 1;
        }
    }
    return output;
}

/// 从标签属性串里取出指定属性的值，支持单引号、双引号与无引号写法。
static NSString *SynorAttribute(NSString *attributes, NSString *name) {
    if (!attributes.length) return nil;
    NSString *lowercased = attributes.lowercaseString;
    NSUInteger searchFrom = 0;
    while (searchFrom < lowercased.length) {
        NSRange found = [lowercased rangeOfString:name options:0 range:NSMakeRange(searchFrom, lowercased.length - searchFrom)];
        if (found.location == NSNotFound) return nil;
        NSUInteger after = NSMaxRange(found);
        unichar previous = found.location == 0 ? ' ' : [lowercased characterAtIndex:found.location - 1];
        if (previous != ' ' && previous != '\t' && previous != '\n') { searchFrom = after; continue; }
        NSUInteger cursor = after;
        while (cursor < attributes.length && ([attributes characterAtIndex:cursor] == ' ' || [attributes characterAtIndex:cursor] == '\t')) cursor++;
        if (cursor >= attributes.length || [attributes characterAtIndex:cursor] != '=') { searchFrom = after; continue; }
        cursor++;
        while (cursor < attributes.length && ([attributes characterAtIndex:cursor] == ' ' || [attributes characterAtIndex:cursor] == '\t')) cursor++;
        if (cursor >= attributes.length) return nil;
        unichar quote = [attributes characterAtIndex:cursor];
        if (quote == '"' || quote == '\'') {
            NSUInteger end = cursor + 1;
            while (end < attributes.length && [attributes characterAtIndex:end] != quote) end++;
            return SynorDecodeEntities([attributes substringWithRange:NSMakeRange(cursor + 1, end - cursor - 1)]);
        }
        NSUInteger end = cursor;
        while (end < attributes.length && [attributes characterAtIndex:end] != ' ' && [attributes characterAtIndex:end] != '\t') end++;
        return SynorDecodeEntities([attributes substringWithRange:NSMakeRange(cursor, end - cursor)]);
    }
    return nil;
}

NSString *SynorMarkdownFromHTML(NSString *html) {
    if (!html.length) return @"";
    NSMutableString *output = [NSMutableString stringWithCapacity:html.length];
    // 列表上下文：是否有序 + 下一个序号，支持嵌套。
    NSMutableArray<NSNumber *> *listOrdered = [NSMutableArray array];
    NSMutableArray<NSNumber *> *listNext = [NSMutableArray array];
    // 链接上下文：href 与链接文字，闭合时再拼成 Markdown。
    NSMutableArray<NSString *> *linkHrefs = [NSMutableArray array];
    NSMutableArray<NSMutableString *> *linkTexts = [NSMutableArray array];
    BOOL inPre = NO;
    BOOL stripLeadingNewline = NO;
    NSInteger skipDepth = 0;

    void (^emit)(NSString *) = ^(NSString *text) {
        if (!text.length) return;
        if (linkTexts.count) [linkTexts.lastObject appendString:text];
        else [output appendString:text];
    };
    // 保证输出结尾至少有 count 个换行；空输出不补前导换行。
    void (^newlines)(NSUInteger) = ^(NSUInteger count) {
        if (!output.length) return;
        NSUInteger trailing = 0;
        while (trailing < output.length && [output characterAtIndex:output.length - trailing - 1] == '\n') trailing++;
        for (NSUInteger index = trailing; index < count; index++) [output appendString:@"\n"];
    };

    NSUInteger index = 0, length = html.length;
    while (index < length) {
        if ([html characterAtIndex:index] != '<') {
            NSUInteger next = [html rangeOfString:@"<" options:0 range:NSMakeRange(index, length - index)].location;
            if (next == NSNotFound) next = length;
            if (skipDepth == 0) {
                NSString *text = SynorDecodeEntities([html substringWithRange:NSMakeRange(index, next - index)]);
                if (stripLeadingNewline) {
                    if ([text hasPrefix:@"\n"]) text = [text substringFromIndex:1];
                    stripLeadingNewline = NO;
                }
                emit(text);
            }
            index = next;
            continue;
        }
        // 扫描到标签结束，属性引号内的 '>' 不算结束。
        NSUInteger cursor = index + 1;
        BOOL inQuote = NO;
        unichar quoteChar = 0;
        while (cursor < length) {
            unichar character = [html characterAtIndex:cursor];
            if (inQuote) { if (character == quoteChar) inQuote = NO; }
            else if (character == '"' || character == '\'') { inQuote = YES; quoteChar = character; }
            else if (character == '>') break;
            cursor++;
        }
        if (cursor >= length) break;
        NSString *raw = [html substringWithRange:NSMakeRange(index + 1, cursor - index - 1)];
        index = cursor + 1;
        if ([raw hasPrefix:@"!"]) continue;  // 注释与 doctype
        BOOL closing = [raw hasPrefix:@"/"];
        NSString *body = closing ? [raw substringFromIndex:1] : raw;
        if ([body hasSuffix:@"/"]) body = [body substringToIndex:body.length - 1];
        NSRange space = [body rangeOfCharacterFromSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
        NSString *name = (space.location == NSNotFound ? body : [body substringToIndex:space.location]).lowercaseString;
        NSString *attributes = (space.location == NSNotFound ? @"" : [body substringFromIndex:space.location]);
        if (!name.length) continue;
        // script / style 等整块跳过。
        if ([name isEqual:@"script"] || [name isEqual:@"style"] || [name isEqual:@"head"] || [name isEqual:@"title"]) {
            if (closing) { if (skipDepth > 0) skipDepth--; }
            else if (![raw hasSuffix:@"/"]) skipDepth++;
            continue;
        }
        if (skipDepth > 0) continue;

        if (closing) {
            if ([name isEqual:@"p"] || [name isEqual:@"table"] ||
                [name isEqual:@"ul"] || [name isEqual:@"ol"] || [name isEqual:@"blockquote"] ||
                [name isEqual:@"h1"] || [name isEqual:@"h2"] || [name isEqual:@"h3"] ||
                [name isEqual:@"h4"] || [name isEqual:@"h5"] || [name isEqual:@"h6"]) {
                newlines(2);
            } else if ([name isEqual:@"div"] || [name isEqual:@"tr"] || [name isEqual:@"li"] || [name isEqual:@"br"]) {
                newlines(1);
            } else if ([name isEqual:@"pre"]) {
                newlines(1); emit(@"```"); newlines(2); inPre = NO;
            } else if ([name isEqual:@"strong"] || [name isEqual:@"b"]) {
                emit(@"**");
            } else if ([name isEqual:@"em"] || [name isEqual:@"i"]) {
                emit(@"*");
            } else if ([name isEqual:@"code"]) {
                if (!inPre) emit(@"`");
            } else if ([name isEqual:@"a"]) {
                NSString *href = linkHrefs.count ? linkHrefs.lastObject : @"";
                NSMutableString *text = linkTexts.count ? linkTexts.lastObject : [NSMutableString string];
                if (linkHrefs.count) [linkHrefs removeLastObject];
                if (linkTexts.count) [linkTexts removeLastObject];
                emit(href.length ? [NSString stringWithFormat:@"[%@](%@)", text, href] : text);
            }
            // 关闭 ul/ol 时弹出列表上下文。
            if ([name isEqual:@"ul"] || [name isEqual:@"ol"]) {
                if (listOrdered.count) [listOrdered removeLastObject];
                if (listNext.count) [listNext removeLastObject];
            }
            continue;
        }

        if ([name isEqual:@"p"]) {
            newlines(2);
        } else if ([name isEqual:@"div"]) {
            newlines(1);
        } else if ([name isEqual:@"br"]) {
            emit(@"\n");
        } else if ([name isEqual:@"strong"] || [name isEqual:@"b"]) {
            emit(@"**");
        } else if ([name isEqual:@"em"] || [name isEqual:@"i"]) {
            emit(@"*");
        } else if ([name isEqual:@"code"]) {
            if (!inPre) emit(@"`");
        } else if ([name isEqual:@"pre"]) {
            newlines(2); emit(@"```\n"); inPre = YES; stripLeadingNewline = YES;
        } else if ([name isEqual:@"blockquote"]) {
            newlines(2); emit(@"> ");
        } else if (name.length == 2 && [name hasPrefix:@"h"] && [name characterAtIndex:1] >= '1' && [name characterAtIndex:1] <= '6') {
            NSUInteger level = [name characterAtIndex:1] - '0';
            newlines(2);
            emit([@"" stringByPaddingToLength:level withString:@"#" startingAtIndex:0]);
            emit(@" ");
        } else if ([name isEqual:@"ul"] || [name isEqual:@"ol"]) {
            newlines(2);
            NSInteger start = 1;
            if ([name isEqual:@"ol"]) {
                NSString *value = SynorAttribute(attributes, @"start");
                if (value.integerValue > 0) start = value.integerValue;
            }
            [listOrdered addObject:@([name isEqual:@"ol"])];
            [listNext addObject:@(start)];
        } else if ([name isEqual:@"li"]) {
            newlines(1);
            if (listOrdered.lastObject.boolValue) {
                NSInteger next = listNext.lastObject.integerValue;
                emit([NSString stringWithFormat:@"%ld. ", (long)next]);
                if (listNext.count) listNext[listNext.count - 1] = @(next + 1);
            } else {
                emit(@"- ");
            }
        } else if ([name isEqual:@"a"]) {
            [linkHrefs addObject:(SynorAttribute(attributes, @"href") ?: @"")];
            [linkTexts addObject:[NSMutableString string]];
        } else if ([name isEqual:@"table"]) {
            newlines(2);
        } else if ([name isEqual:@"td"] || [name isEqual:@"th"]) {
            if (output.length && [output characterAtIndex:output.length - 1] != '\n') emit(@" | ");
        } else if ([name isEqual:@"hr"]) {
            newlines(2); emit(@"---"); newlines(2);
        } else if ([name isEqual:@"img"]) {
            NSString *source = SynorAttribute(attributes, @"src");
            if (source.length) emit([NSString stringWithFormat:@"![%@](%@)", SynorAttribute(attributes, @"alt") ?: @"", source]);
        }
        // 其余标签只丢弃标记，保留其中的文字。
    }

    // 收尾：去掉行尾空白；围栏代码块内保留空行，其余地方把连续空行压成一个。
    NSMutableString *cleaned = [NSMutableString stringWithCapacity:output.length];
    BOOL inFence = NO;
    NSInteger consecutiveBlanks = 0;
    for (NSString *line in [output componentsSeparatedByString:@"\n"]) {
        NSUInteger end = line.length;
        while (end > 0) {
            unichar character = [line characterAtIndex:end - 1];
            if (character == ' ' || character == '\t') end--; else break;
        }
        NSString *trimmed = [line substringToIndex:end];
        if ([trimmed hasPrefix:@"```"]) {
            inFence = !inFence;
            consecutiveBlanks = 0;
            [cleaned appendFormat:@"%@\n", trimmed];
            continue;
        }
        if (!inFence && !trimmed.length) {
            consecutiveBlanks++;
            if (consecutiveBlanks > 1) continue;
        } else {
            consecutiveBlanks = 0;
        }
        [cleaned appendFormat:@"%@\n", trimmed];
    }
    return [cleaned stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
}
