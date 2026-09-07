//! 帧解析表达式引擎 —— frame definition `mode:"script"` 的求值核心(spec B.8)。
//!
//! 设计边界(沙箱即语言本身):
//! - 数值单类型 f64(位运算内部转 i64),不可表达赋值/循环/函数定义/IO;
//! - 变量只有 `frame[i]` 与 `len`,函数全部为纯函数;
//! - 限额: 单表达式 ≤256 字符、AST 深度 ≤16、求值步数 ≤4096
//!   (区间函数 sum/xor/crc16 按覆盖字节数计步,保证任意帧长下解析耗时受限),
//!   超限/除零/索引越界/非有限结果一律显式错误码,绝不静默。
//!
//! 运算符优先级与 C 一致(低→高): `?:` `||` `&&` `|` `^` `&`
//! `== !=` `< <= > >=` `<< >>` `+ -` `* / %` 一元 `- ! ~`。三目右结合。

use serde::Serialize;

use crate::modbus_rtu::crc16_modbus;

pub const MAX_EXPR_LEN: usize = 256;
pub const MAX_EXPR_DEPTH: usize = 16;
pub const MAX_EXPR_STEPS: usize = 4_096;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExprError {
    pub code: String,
    pub message: String,
}

impl ExprError {
    fn syntax(message: impl Into<String>) -> Self {
        Self {
            code: "EXPR_SYNTAX".to_string(),
            message: message.into(),
        }
    }
    fn eval(message: impl Into<String>) -> Self {
        Self {
            code: "EXPR_EVAL".to_string(),
            message: message.into(),
        }
    }
    fn limit(message: impl Into<String>) -> Self {
        Self {
            code: "EXPR_LIMIT".to_string(),
            message: message.into(),
        }
    }
}

/// 内置函数与其参数个数(全纯函数)。
const FUNCTIONS: &[(&str, usize)] = &[
    ("abs", 1),
    ("bcd", 1),
    ("bit", 2),
    ("min", 2),
    ("max", 2),
    ("sum", 2),
    ("xor", 2),
    ("crc16", 2),
];

#[derive(Debug, Clone, PartialEq)]
enum Tok {
    Num(f64),
    Ident(String),
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Amp,
    Pipe,
    Caret,
    Shl,
    Shr,
    Tilde,
    EqEq,
    NotEq,
    Lt,
    Le,
    Gt,
    Ge,
    AndAnd,
    OrOr,
    Not,
    Question,
    Colon,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Comma,
}

fn tokenize(src: &str) -> Result<Vec<Tok>, ExprError> {
    let chars: Vec<char> = src.chars().collect();
    let mut toks = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        if c.is_whitespace() {
            i += 1;
            continue;
        }
        // 十六进制 0x… / 十进制(可带小数点)
        if c.is_ascii_digit() || (c == '.' && i + 1 < chars.len() && chars[i + 1].is_ascii_digit())
        {
            let start = i;
            if c == '0' && i + 1 < chars.len() && (chars[i + 1] == 'x' || chars[i + 1] == 'X') {
                i += 2;
                let hex_start = i;
                while i < chars.len() && chars[i].is_ascii_hexdigit() {
                    i += 1;
                }
                if i == hex_start {
                    return Err(ExprError::syntax("十六进制字面量没有数字"));
                }
                let text: String = chars[hex_start..i].iter().collect();
                let value = u64::from_str_radix(&text, 16)
                    .map_err(|_| ExprError::syntax(format!("十六进制字面量过大: 0x{text}")))?;
                toks.push(Tok::Num(value as f64));
                continue;
            }
            let mut seen_dot = false;
            while i < chars.len() && (chars[i].is_ascii_digit() || (chars[i] == '.' && !seen_dot)) {
                if chars[i] == '.' {
                    seen_dot = true;
                }
                i += 1;
            }
            let text: String = chars[start..i].iter().collect();
            let value: f64 = text
                .parse()
                .map_err(|_| ExprError::syntax(format!("数字字面量非法: {text}")))?;
            if !value.is_finite() {
                return Err(ExprError::syntax(format!("数字字面量非法: {text}")));
            }
            toks.push(Tok::Num(value));
            continue;
        }
        if c.is_ascii_alphabetic() || c == '_' {
            let start = i;
            while i < chars.len() && (chars[i].is_ascii_alphanumeric() || chars[i] == '_') {
                i += 1;
            }
            toks.push(Tok::Ident(chars[start..i].iter().collect()));
            continue;
        }
        let peek2 = |a: char, b: char| i + 1 < chars.len() && chars[i] == a && chars[i + 1] == b;
        let (tok, width) = match c {
            '+' => (Tok::Plus, 1),
            '-' => (Tok::Minus, 1),
            '*' => (Tok::Star, 1),
            '/' => (Tok::Slash, 1),
            '%' => (Tok::Percent, 1),
            '(' => (Tok::LParen, 1),
            ')' => (Tok::RParen, 1),
            '[' => (Tok::LBracket, 1),
            ']' => (Tok::RBracket, 1),
            ',' => (Tok::Comma, 1),
            '?' => (Tok::Question, 1),
            ':' => (Tok::Colon, 1),
            '~' => (Tok::Tilde, 1),
            '^' => (Tok::Caret, 1),
            '&' if peek2('&', '&') => (Tok::AndAnd, 2),
            '&' => (Tok::Amp, 1),
            '|' if peek2('|', '|') => (Tok::OrOr, 2),
            '|' => (Tok::Pipe, 1),
            '<' if peek2('<', '<') => (Tok::Shl, 2),
            '<' if peek2('<', '=') => (Tok::Le, 2),
            '<' => (Tok::Lt, 1),
            '>' if peek2('>', '>') => (Tok::Shr, 2),
            '>' if peek2('>', '=') => (Tok::Ge, 2),
            '>' => (Tok::Gt, 1),
            '=' if peek2('=', '=') => (Tok::EqEq, 2),
            '!' if peek2('!', '=') => (Tok::NotEq, 2),
            '!' => (Tok::Not, 1),
            other => return Err(ExprError::syntax(format!("非法字符: {other}"))),
        };
        toks.push(tok);
        i += width;
    }
    Ok(toks)
}

#[derive(Debug)]
enum Node {
    Num(f64),
    FrameIndex(Box<Node>),
    Len,
    Call(&'static str, Vec<Node>),
    Unary(&'static str, Box<Node>),
    Binary(&'static str, Box<Node>, Box<Node>),
    Ternary(Box<Node>, Box<Node>, Box<Node>),
}

/// 编译后的表达式。编译期完成词法/语法/未知标识符与参数个数检查;
/// 求值期只可能出: 索引越界/除零/位运算非整数/移位越界/非有限结果/步数超限。
#[derive(Debug)]
pub struct Expr {
    root: Node,
}

struct Parser {
    toks: Vec<Tok>,
    pos: usize,
}

impl Parser {
    fn peek(&self) -> Option<&Tok> {
        self.toks.get(self.pos)
    }
    fn next(&mut self) -> Option<Tok> {
        let t = self.toks.get(self.pos).cloned();
        if t.is_some() {
            self.pos += 1;
        }
        t
    }
    fn expect(&mut self, want: &Tok, what: &str) -> Result<(), ExprError> {
        match self.next() {
            Some(t) if &t == want => Ok(()),
            other => Err(ExprError::syntax(format!(
                "{what} 期望 {want:?}, 实际 {other:?}"
            ))),
        }
    }

    fn parse_expr(&mut self, min_bp: u8, depth: usize) -> Result<Node, ExprError> {
        if depth > MAX_EXPR_DEPTH {
            return Err(ExprError::limit(format!(
                "表达式嵌套超过 {MAX_EXPR_DEPTH} 层"
            )));
        }
        let mut lhs = self.parse_unary(depth)?;
        loop {
            let Some(tok) = self.peek().cloned() else {
                break;
            };
            if tok == Tok::Question {
                // 三目右结合、优先级最低: 外层上下文更高时不吃 '?'
                if min_bp > 1 {
                    break;
                }
                self.pos += 1;
                let then = self.parse_expr(1, depth + 1)?;
                self.expect(&Tok::Colon, "三目表达式")?;
                let alt = self.parse_expr(1, depth + 1)?;
                lhs = Node::Ternary(Box::new(lhs), Box::new(then), Box::new(alt));
                // 三目是最低优先级,构建后同层不会再有更低运算符
                break;
            }
            let (op, lbp, rbp): (&'static str, u8, u8) = match tok {
                Tok::OrOr => ("||", 2, 3),
                Tok::AndAnd => ("&&", 4, 5),
                Tok::Pipe => ("|", 6, 7),
                Tok::Caret => ("^", 8, 9),
                Tok::Amp => ("&", 10, 11),
                Tok::EqEq => ("==", 12, 13),
                Tok::NotEq => ("!=", 12, 13),
                Tok::Lt => ("<", 14, 15),
                Tok::Le => ("<=", 14, 15),
                Tok::Gt => (">", 14, 15),
                Tok::Ge => (">=", 14, 15),
                Tok::Shl => ("<<", 16, 17),
                Tok::Shr => (">>", 16, 17),
                Tok::Plus => ("+", 18, 19),
                Tok::Minus => ("-", 18, 19),
                Tok::Star => ("*", 20, 21),
                Tok::Slash => ("/", 20, 21),
                Tok::Percent => ("%", 20, 21),
                _ => break,
            };
            if lbp < min_bp {
                break;
            }
            self.pos += 1;
            let rhs = self.parse_expr(rbp, depth + 1)?;
            lhs = Node::Binary(op, Box::new(lhs), Box::new(rhs));
        }
        Ok(lhs)
    }

    fn parse_unary(&mut self, depth: usize) -> Result<Node, ExprError> {
        if depth > MAX_EXPR_DEPTH {
            return Err(ExprError::limit(format!(
                "表达式嵌套超过 {MAX_EXPR_DEPTH} 层"
            )));
        }
        match self.peek() {
            Some(Tok::Minus) => {
                self.pos += 1;
                let inner = self.parse_unary(depth + 1)?;
                Ok(Node::Unary("-", Box::new(inner)))
            }
            Some(Tok::Not) => {
                self.pos += 1;
                let inner = self.parse_unary(depth + 1)?;
                Ok(Node::Unary("!", Box::new(inner)))
            }
            Some(Tok::Tilde) => {
                self.pos += 1;
                let inner = self.parse_unary(depth + 1)?;
                Ok(Node::Unary("~", Box::new(inner)))
            }
            _ => self.parse_primary(depth),
        }
    }

    fn parse_primary(&mut self, depth: usize) -> Result<Node, ExprError> {
        if depth > MAX_EXPR_DEPTH {
            return Err(ExprError::limit(format!(
                "表达式嵌套超过 {MAX_EXPR_DEPTH} 层"
            )));
        }
        match self.next() {
            Some(Tok::Num(v)) => Ok(Node::Num(v)),
            Some(Tok::Ident(name)) => match name.as_str() {
                "len" => Ok(Node::Len),
                "frame" => {
                    self.expect(&Tok::LBracket, "frame 索引")?;
                    let idx = self.parse_expr(0, depth + 1)?;
                    self.expect(&Tok::RBracket, "frame 索引")?;
                    Ok(Node::FrameIndex(Box::new(idx)))
                }
                func => {
                    let Some((canonical, arity)) =
                        FUNCTIONS.iter().find(|(f, _)| *f == func).copied()
                    else {
                        return Err(ExprError::syntax(format!("未知标识符: {func}")));
                    };
                    self.expect(&Tok::LParen, "函数调用")?;
                    let mut args = Vec::new();
                    if !matches!(self.peek(), Some(Tok::RParen)) {
                        loop {
                            args.push(self.parse_expr(0, depth + 1)?);
                            match self.peek() {
                                Some(Tok::Comma) => {
                                    self.pos += 1;
                                }
                                Some(Tok::RParen) => break,
                                other => {
                                    return Err(ExprError::syntax(format!(
                                        "函数参数后期望 , 或 ), 实际 {other:?}"
                                    )));
                                }
                            }
                        }
                    }
                    self.expect(&Tok::RParen, "函数调用")?;
                    if args.len() != arity {
                        return Err(ExprError::syntax(format!(
                            "函数 {canonical} 需要 {arity} 个参数, 实际 {}",
                            args.len()
                        )));
                    }
                    Ok(Node::Call(canonical, args))
                }
            },
            Some(Tok::LParen) => {
                let inner = self.parse_expr(0, depth + 1)?;
                self.expect(&Tok::RParen, "括号")?;
                Ok(inner)
            }
            other => Err(ExprError::syntax(format!(
                "期望数字/变量/函数/(, 实际 {other:?}"
            ))),
        }
    }
}

/// 编译表达式(不依赖帧)。语法/未知标识符/参数个数错误在此暴露。
pub fn compile(src: &str) -> Result<Expr, ExprError> {
    let trimmed = src.trim();
    if trimmed.is_empty() {
        return Err(ExprError::syntax("表达式为空"));
    }
    if src.chars().count() > MAX_EXPR_LEN {
        return Err(ExprError::limit(format!("表达式超过 {MAX_EXPR_LEN} 字符")));
    }
    let toks = tokenize(trimmed)?;
    if toks.is_empty() {
        return Err(ExprError::syntax("表达式为空"));
    }
    let mut parser = Parser { toks, pos: 0 };
    let root = parser.parse_expr(0, 0)?;
    if parser.pos != parser.toks.len() {
        return Err(ExprError::syntax(format!(
            "表达式结尾有多余内容: {:?}",
            parser.toks[parser.pos]
        )));
    }
    Ok(Expr { root })
}

struct EvalCtx<'a> {
    frame: &'a [u8],
    steps: usize,
}

impl EvalCtx<'_> {
    fn step(&mut self) -> Result<(), ExprError> {
        self.steps += 1;
        if self.steps > MAX_EXPR_STEPS {
            return Err(ExprError::limit(format!("求值步数超过 {MAX_EXPR_STEPS}")));
        }
        Ok(())
    }
}

fn as_int(value: f64, what: &str) -> Result<i64, ExprError> {
    if !value.is_finite() || value.fract() != 0.0 {
        return Err(ExprError::eval(format!("{what} 要求整数, 实际 {value}")));
    }
    if !(-9_223_372_036_854_775_808.0..=9_223_372_036_854_775_807.0).contains(&value) {
        return Err(ExprError::eval(format!("{what} 超出整数范围: {value}")));
    }
    Ok(value as i64)
}

/// 区间函数(sum/xor/crc16)的字节区间,双端闭区间。
fn frame_range(frame: &[u8], a: f64, b: f64, func: &str) -> Result<(usize, usize), ExprError> {
    let start = as_int(a, func)?;
    let end = as_int(b, func)?;
    if start < 0 || end < start || end as usize >= frame.len() {
        return Err(ExprError::eval(format!(
            "{func} 区间非法: [{start}, {end}], 帧长 {}",
            frame.len()
        )));
    }
    Ok((start as usize, end as usize))
}

fn eval_node(node: &Node, ctx: &mut EvalCtx) -> Result<f64, ExprError> {
    ctx.step()?;
    let value = match node {
        Node::Num(v) => *v,
        Node::Len => ctx.frame.len() as f64,
        Node::FrameIndex(idx) => {
            let i = as_int(eval_node(idx, ctx)?, "frame 索引")?;
            if i < 0 || i as usize >= ctx.frame.len() {
                return Err(ExprError::eval(format!(
                    "frame 索引越界: {i}, 帧长 {}",
                    ctx.frame.len()
                )));
            }
            ctx.frame[i as usize] as f64
        }
        Node::Unary(op, inner) => {
            let v = eval_node(inner, ctx)?;
            match *op {
                "-" => -v,
                "!" => ((v == 0.0) as i32) as f64,
                "~" => (!(as_int(v, "~")?)) as f64,
                _ => unreachable!(),
            }
        }
        Node::Binary(op, lhs, rhs) => match *op {
            "&&" => {
                // 短路: 左假(=0)时右侧不求值(C 语义)
                if eval_node(lhs, ctx)? == 0.0 {
                    0.0
                } else {
                    ((eval_node(rhs, ctx)? != 0.0) as i32) as f64
                }
            }
            "||" => {
                if eval_node(lhs, ctx)? != 0.0 {
                    1.0
                } else {
                    ((eval_node(rhs, ctx)? != 0.0) as i32) as f64
                }
            }
            _ => {
                let l = eval_node(lhs, ctx)?;
                let r = eval_node(rhs, ctx)?;
                match *op {
                    "+" => l + r,
                    "-" => l - r,
                    "*" => l * r,
                    "/" => {
                        if r == 0.0 {
                            return Err(ExprError::eval("除数为 0"));
                        }
                        l / r
                    }
                    "%" => {
                        let li = as_int(l, "%")?;
                        let ri = as_int(r, "%")?;
                        if ri == 0 {
                            return Err(ExprError::eval("除数为 0"));
                        }
                        (li % ri) as f64
                    }
                    "&" => (as_int(l, "&")? & as_int(r, "&")?) as f64,
                    "|" => (as_int(l, "|")? | as_int(r, "|")?) as f64,
                    "^" => (as_int(l, "^")? ^ as_int(r, "^")?) as f64,
                    "<<" => {
                        let li = as_int(l, "<<")?;
                        let ri = as_int(r, "<<")?;
                        if !(0..64).contains(&ri) {
                            return Err(ExprError::eval(format!("移位数非法: {ri}")));
                        }
                        ((li as u64) << ri) as f64
                    }
                    ">>" => {
                        let li = as_int(l, ">>")?;
                        let ri = as_int(r, ">>")?;
                        if !(0..64).contains(&ri) {
                            return Err(ExprError::eval(format!("移位数非法: {ri}")));
                        }
                        (li >> ri) as f64
                    }
                    "==" => ((l == r) as i32) as f64,
                    "!=" => ((l != r) as i32) as f64,
                    "<" => ((l < r) as i32) as f64,
                    "<=" => ((l <= r) as i32) as f64,
                    ">" => ((l > r) as i32) as f64,
                    ">=" => ((l >= r) as i32) as f64,
                    _ => unreachable!(),
                }
            }
        },
        Node::Ternary(cond, then, alt) => {
            if eval_node(cond, ctx)? != 0.0 {
                eval_node(then, ctx)?
            } else {
                eval_node(alt, ctx)?
            }
        }
        Node::Call(func, args) => {
            let mut vals = Vec::with_capacity(args.len());
            for arg in args {
                vals.push(eval_node(arg, ctx)?);
            }
            match (*func, vals.as_slice()) {
                ("abs", [x]) => x.abs(),
                ("min", [a, b]) => a.min(*b),
                ("max", [a, b]) => a.max(*b),
                ("bit", [x, n]) => {
                    let xi = as_int(*x, "bit")?;
                    let ni = as_int(*n, "bit")?;
                    if !(0..64).contains(&ni) {
                        return Err(ExprError::eval(format!("bit 位数非法: {ni}")));
                    }
                    (((xi as u64) >> ni) & 1) as f64
                }
                ("bcd", [x]) => {
                    let xi = as_int(*x, "bcd")?;
                    if !(0..=0xFFFF_FFFF).contains(&xi) {
                        return Err(ExprError::eval(format!("bcd 输入超出 32 位: {xi}")));
                    }
                    let mut value: i64 = 0;
                    let mut place: i64 = 1;
                    let mut rest = xi as u32;
                    while rest > 0 {
                        let digit = rest & 0xF;
                        if digit > 9 {
                            return Err(ExprError::eval(format!("bcd 输入含非法数字: {xi:#x}")));
                        }
                        value += digit as i64 * place;
                        place *= 10;
                        rest >>= 4;
                    }
                    value as f64
                }
                ("sum", [a, b]) => {
                    let (start, end) = frame_range(ctx.frame, *a, *b, "sum")?;
                    let mut acc: i64 = 0;
                    for byte in &ctx.frame[start..=end] {
                        ctx.step()?;
                        acc += *byte as i64;
                    }
                    acc as f64
                }
                ("xor", [a, b]) => {
                    let (start, end) = frame_range(ctx.frame, *a, *b, "xor")?;
                    let mut acc: i64 = 0;
                    for byte in &ctx.frame[start..=end] {
                        ctx.step()?;
                        acc ^= *byte as i64;
                    }
                    acc as f64
                }
                ("crc16", [a, b]) => {
                    let (start, end) = frame_range(ctx.frame, *a, *b, "crc16")?;
                    for _ in start..=end {
                        ctx.step()?;
                    }
                    crc16_modbus(&ctx.frame[start..=end]) as f64
                }
                _ => unreachable!(),
            }
        }
    };
    if !value.is_finite() {
        return Err(ExprError::eval(format!("求值结果非有限: {value}")));
    }
    Ok(value)
}

impl Expr {
    /// 对帧求值。求值期错误只有: 索引越界/除零/位运算非整数/移位越界/非有限结果/步数超限。
    pub fn eval(&self, frame: &[u8]) -> Result<f64, ExprError> {
        let mut ctx = EvalCtx { frame, steps: 0 };
        eval_node(&self.root, &mut ctx)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ok(src: &str, frame: &[u8]) -> f64 {
        compile(src)
            .unwrap_or_else(|e| panic!("compile {src}: {e:?}"))
            .eval(frame)
            .unwrap_or_else(|e| panic!("eval {src}: {e:?}"))
    }
    fn err(src: &str, frame: &[u8]) -> ExprError {
        let expr = compile(src).unwrap_or_else(|e| panic!("compile {src}: {e:?}"));
        expr.eval(frame).expect_err("eval should fail")
    }

    #[test]
    fn arithmetic_precedence_and_hex() {
        assert_eq!(ok("1+2*3", &[]), 7.0);
        assert_eq!(ok("(1+2)*3", &[]), 9.0);
        assert_eq!(ok("0x0F + 1", &[]), 16.0);
        assert_eq!(ok("7 % 3", &[]), 1.0);
        assert_eq!(ok("10 / 4", &[]), 2.5);
        assert_eq!(ok("1.5 + .5", &[]), 2.0);
        assert_eq!(ok("-7 % 3", &[]), -1.0);
    }

    #[test]
    fn frame_index_len_and_bool() {
        let frame = [0x55, 0x25, 0x10];
        assert_eq!(ok("frame[0]", &frame), 85.0);
        assert_eq!(ok("frame[len-1]", &frame), 16.0);
        assert_eq!(ok("frame[0] == 0x55", &frame), 1.0);
        assert_eq!(ok("frame[0] != 0x55", &frame), 0.0);
        assert_eq!(ok("len >= 3", &frame), 1.0);
        assert_eq!(err("frame[3]", &frame).code, "EXPR_EVAL");
        assert_eq!(err("frame[len]", &frame).code, "EXPR_EVAL");
        assert_eq!(err("frame[0-1]", &frame).code, "EXPR_EVAL");
    }

    #[test]
    fn bitwise_shift_unary_and_c_precedence() {
        assert_eq!(ok("0xF0 & 0x0F", &[]), 0.0);
        assert_eq!(ok("0xF0 | 0x0F", &[]), 255.0);
        assert_eq!(ok("0xFF ^ 0x0F", &[]), 240.0);
        assert_eq!(ok("1 << 10", &[]), 1024.0);
        assert_eq!(ok("0x100 >> 4", &[]), 16.0);
        assert_eq!(ok("~0", &[]), -1.0);
        assert_eq!(ok("-5 + 3", &[]), -2.0);
        assert_eq!(ok("!0 && 1", &[]), 1.0);
        // C 优先级: == 高于 & → 1 & (2==2) = 1;若 & 更高则是 (1&2)==2 = 0
        assert_eq!(ok("1 & 2 == 2", &[]), 1.0);
        assert_eq!(ok("4 & 2 == 2", &[]), 0.0); // 4 & (2==2) = 4&1 = 0
    }

    #[test]
    fn ternary_right_assoc_and_short_circuit() {
        assert_eq!(ok("1 ? 10 : 20", &[]), 10.0);
        assert_eq!(ok("0 ? 10 : 20", &[]), 20.0);
        // 右结合: 1 ? 0 : (1 ? 99 : 88) = 0;左结合错误会得到 (1?0:1)?99:88 = 88
        assert_eq!(ok("1 ? 0 : 1 ? 99 : 88", &[]), 0.0);
        // 短路: 右侧除零不求值
        assert_eq!(ok("0 && 1/0", &[]), 0.0);
        assert_eq!(ok("1 || 1/0", &[]), 1.0);
        assert_eq!(err("1 && 1/0", &[]).code, "EXPR_EVAL");
    }

    #[test]
    fn builtin_functions() {
        let frame = [0x01, 0x03, 0x25, 0x12];
        assert_eq!(ok("bit(0x08, 3)", &frame), 1.0);
        assert_eq!(ok("bit(0x08, 2)", &frame), 0.0);
        assert_eq!(ok("bcd(frame[2])", &frame), 25.0);
        assert_eq!(ok("bcd(0x12)", &frame), 12.0);
        assert_eq!(ok("bcd(0x0)", &frame), 0.0);
        assert_eq!(ok("sum(0, len-1)", &frame), (1 + 3 + 0x25 + 0x12) as f64);
        assert_eq!(ok("xor(0, 1)", &frame), (0x01 ^ 0x03) as f64);
        assert_eq!(ok("crc16(0, 1)", &frame), crc16_modbus(&frame[..=1]) as f64);
        assert_eq!(ok("abs(0 - 5)", &frame), 5.0);
        assert_eq!(ok("min(3, 2)", &frame), 2.0);
        assert_eq!(ok("max(3, 2)", &frame), 3.0);
        assert_eq!(err("bcd(0x2A)", &frame).code, "EXPR_EVAL");
        assert_eq!(err("sum(2, 1)", &frame).code, "EXPR_EVAL");
        assert_eq!(err("sum(0, len)", &frame).code, "EXPR_EVAL");
        assert_eq!(err("bit(1, 64)", &frame).code, "EXPR_EVAL");
    }

    #[test]
    fn compile_errors() {
        assert_eq!(compile("").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("   ").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("1 +").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("(1").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("foo").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("frobnicate(1)").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("1 2").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("0x").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("min(1)").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("sum(1,2,3)").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("frame[0").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("1 ? 2").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("1 @ 2").unwrap_err().code, "EXPR_SYNTAX");
        assert_eq!(compile("1 = 2").unwrap_err().code, "EXPR_SYNTAX");
        let long = "1+".repeat(130); // 260 字符
        assert_eq!(compile(&long).unwrap_err().code, "EXPR_LIMIT");
        let deep = format!("{}1{}", "(".repeat(20), ")".repeat(20));
        assert_eq!(compile(&deep).unwrap_err().code, "EXPR_LIMIT");
        // 256 字符以内的最长链编译且求值正常(步数上限不该误伤): 128 个 1 相加
        let chain = "1+1".to_string() + &"+1".repeat(126); // 255 字符
        assert_eq!(ok(&chain, &[]), 128.0);
    }

    #[test]
    fn eval_limits_and_nonfinite() {
        let frame = [0x01];
        // 步数超限: 区间函数按字节数计步,512 字节帧 × 多次全帧求和
        let big_frame = vec![0x55u8; 512];
        let heavy = "sum(0,511)".to_string() + &"+sum(0,511)".repeat(22); // 252 字符
        let e = compile(&heavy).unwrap().eval(&big_frame).unwrap_err();
        assert_eq!(e.code, "EXPR_LIMIT");
        // 非有限结果: 2^62 连乘 17 次 = 2^1054 超出 f64 范围
        let big = "(0x1<<62)".to_string() + &" * (0x1<<62)".repeat(16); // 218 字符
        let e = compile(&big).unwrap().eval(&frame).unwrap_err();
        assert_eq!(e.code, "EXPR_EVAL");
        assert_eq!(err("1/0", &frame).code, "EXPR_EVAL");
        assert_eq!(err("1%0", &frame).code, "EXPR_EVAL");
        assert_eq!(err("1.5 & 1", &frame).code, "EXPR_EVAL");
        assert_eq!(err("1 << 64", &frame).code, "EXPR_EVAL");
    }
}
