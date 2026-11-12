# tereditor shell integration — written for tereditor.
# Part of tereditor and distributed under tereditor's license.
#
# The terminal host starts bash as `bash --posix -l` with ENV naming this file, which POSIX mode reads in interactive
# shells. With TEREDITOR_SHELL_INJECT set, it leaves POSIX mode, restores ENV and HISTFILE, and reads the startup files
# bash would have read itself. In interactive shells it then marks the prompt (OSC 133 A/B), command start (C) and end
# with its status (D), and reports the working directory (OSC 7). It may also be sourced directly from a .bashrc.

# The user's startup files are found at run time.
# shellcheck disable=SC1090,SC1091

if [ -n "${TEREDITOR_SHELL_INJECT-}" ]; then
  unset TEREDITOR_SHELL_INJECT
  set +o posix
  if [ -n "${TEREDITOR_SHELL_BASH_ENV+set}" ]; then
    export ENV="$TEREDITOR_SHELL_BASH_ENV"
    unset TEREDITOR_SHELL_BASH_ENV
  else
    unset ENV
  fi
  if [ -n "${TEREDITOR_SHELL_HISTFILE_UNEXPORT-}" ]; then
    unset TEREDITOR_SHELL_HISTFILE_UNEXPORT
    export -n HISTFILE
  fi
  # `.` restores the DEBUG trap on return unless functrace is on; keep any trap the user's files set.
  case $- in *T*) _tereditor_functrace=1 ;; *) _tereditor_functrace= ; set -T ;; esac
  if shopt -q login_shell; then
    [ -r /etc/profile ] && . /etc/profile
    for _tereditor_rc in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
      if [ -r "$_tereditor_rc" ]; then . "$_tereditor_rc"; break; fi
    done
    unset _tereditor_rc
  else
    [ -r /etc/bash.bashrc ] && . /etc/bash.bashrc
    [ -r "$HOME/.bashrc" ] && . "$HOME/.bashrc"
  fi
  [ -z "$_tereditor_functrace" ] && set +T
  unset _tereditor_functrace
fi

if [[ $- == *i* && -z ${_tereditor_integrated-} ]]; then
  _tereditor_integrated=1
  # 1 while the prompt is up; the first command after it clears it and marks C, so an empty Enter reports nothing.
  _tereditor_at_prompt=
  _tereditor_cwd=
  _tereditor_trapped=
  _tereditor_install=
  _tereditor_mark_a=$'\[\e]133;A\a\]'
  _tereditor_mark_b=$'\[\e]133;B\a\]'

  _tereditor_report_cwd() {
    local LC_ALL=C dir=$PWD out='' c i
    for (( i = 0; i < ${#dir}; i++ )); do
      c=${dir:i:1}
      case $c in
        [-/._~A-Za-z0-9]) out+=$c ;;
        *) printf -v c '%%%02X' "'$c"; out+=$c ;;
      esac
    done
    printf '\e]7;file://%s%s\a' "${HOSTNAME%%.*}" "$out"
  }

  # First in PROMPT_COMMAND, so $? is still the command's status.
  _tereditor_precmd() {
    local ret=$?
    if [[ $_tereditor_at_prompt == 0 ]]; then printf '\e]133;D;%s\a' "$ret"; fi
    _tereditor_at_prompt=
    if [[ $PWD != "$_tereditor_cwd" ]]; then _tereditor_cwd=$PWD; _tereditor_report_cwd; fi
    return $ret
  }

  # Last in PROMPT_COMMAND, after anything that rebuilds PS1.
  _tereditor_prompt() {
    local ret=$?
    if [[ $PS1 != *$'\e]133;A\a'* ]]; then PS1=$_tereditor_mark_a$PS1$_tereditor_mark_b; fi
    return $ret
  }

  _tereditor_debug() {
    if [[ $_tereditor_at_prompt == 1 && $BASH_COMMAND != _tereditor_precmd* ]]; then
      _tereditor_at_prompt=0
      printf '\e]133;C\a'
    fi
  }

  # The DEBUG trap goes in from the first prompt, at top level: bash restores the DEBUG trap when a function or a
  # sourced file that set it returns, and a function does not see it. A DEBUG trap the user already set (as `trap -p` printed it, its third word the
  # action) keeps running, after ours.
  _tereditor_trap_command() {
    _tereditor_trapped=1
    _tereditor_install=
    eval "set -- $1"
    case $3 in *_tereditor_debug*) return ;; esac
    printf -v _tereditor_install 'trap %q DEBUG' "_tereditor_debug${3:+; $3}"
  }

  # The prompt is up only after the last step, so none of these steps counts as a command.
  PROMPT_COMMAND="_tereditor_precmd${PROMPT_COMMAND:+; $PROMPT_COMMAND}; _tereditor_prompt; [[ -n \$_tereditor_trapped ]] || _tereditor_trap_command \"\$(trap -p DEBUG)\"; eval \"\$_tereditor_install\"; _tereditor_install=; _tereditor_at_prompt=1"
fi
