/*global navigator*/
'use strict';

const {
  REGEX_BACKSLASH,
  REGEX_SPECIAL_CHARS,
  REGEX_SPECIAL_CHARS_GLOBAL
} = require('./constants');

exports.isObject = val => val !== null && typeof val === 'object' && !Array.isArray(val);
exports.hasRegexChars = str => REGEX_SPECIAL_CHARS.test(str);
exports.isRegexChar = str => str.length === 1 && exports.hasRegexChars(str);
exports.escapeRegex = str => str.replace(REGEX_SPECIAL_CHARS_GLOBAL, '\\$1');
exports.toPosixSlashes = str => str.replace(REGEX_BACKSLASH, '/');

exports.isWindows = () => {
  if (typeof navigator !== 'undefined' && navigator.platform) {
    const platform = navigator.platform.toLowerCase();
    return platform === 'win32' || platform === 'windows';
  }

  if (typeof process !== 'undefined' && process.platform) {
    return process.platform === 'win32';
  }

  return false;
};

// Preserve the original bracket/escape semantics without rescanning the
// remaining suffix for every unmatched '[' (CodeQL polynomial ReDoS finding).
exports.removeBackslashes = str => {
  const length = str.length;
  const nextClose = new Uint32Array(length + 1);
  const nextBreak = new Uint32Array(length + 1);
  nextClose[length] = nextBreak[length] = length;
  const isLineBreak = ch => ch === '\n' || ch === '\r' || ch === '\u2028' || ch === '\u2029';

  for (let i = length - 1; i >= 0; i--) {
    nextClose[i] = str[i] === ']' && str[i - 1] !== '\\' ? i : nextClose[i + 1];
    nextBreak[i] = isLineBreak(str[i]) ? i : nextBreak[i + 1];
  }

  const output = [];
  for (let i = 0; i < length; i++) {
    if (str[i] === '[' && i + 2 < length) {
      const close = nextClose[i + 2];
      // The old .*? cannot cross a line terminator, but its final [^\\]
      // can consume one immediately before the closing bracket.
      if (close < length && nextBreak[i + 1] >= close - 1) {
        output.push(str.slice(i, close + 1));
        i = close;
        continue;
      }
    }
    if (str[i] !== '\\' || i + 1 === length || isLineBreak(str[i + 1])) {
      output.push(str[i]);
    }
  }
  return output.join('');
};

exports.escapeLast = (input, char, lastIdx) => {
  const idx = input.lastIndexOf(char, lastIdx);
  if (idx === -1) return input;
  if (input[idx - 1] === '\\') return exports.escapeLast(input, char, idx - 1);
  return `${input.slice(0, idx)}\\${input.slice(idx)}`;
};

exports.removePrefix = (input, state = {}) => {
  let output = input;
  if (output.startsWith('./')) {
    output = output.slice(2);
    state.prefix = './';
  }
  return output;
};

exports.wrapOutput = (input, state = {}, options = {}) => {
  const prepend = options.contains ? '' : '^';
  const append = options.contains ? '' : '$';

  let output = `${prepend}(?:${input})${append}`;
  if (state.negated === true) {
    output = `(?:^(?!${output}).*$)`;
  }
  return output;
};

exports.basename = (path, { windows } = {}) => {
  const segs = path.split(windows ? /[\\/]/ : '/');
  const last = segs[segs.length - 1];

  if (last === '') {
    return segs[segs.length - 2];
  }

  return last;
};
