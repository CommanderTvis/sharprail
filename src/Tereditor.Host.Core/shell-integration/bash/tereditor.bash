# SharpRail shell integration — written for SharpRail.
# Part of SharpRail and distributed under SharpRail's license.
#
# The terminal host starts bash as `bash --posix -l` with ENV naming this file, which POSIX mode reads in interactive
# shells. With SHARPRAIL_SHELL_INJECT set, it leaves POSIX mode, restores ENV and HISTFILE, and reads the startup files
# bash would have read itself. In interactive shells it then marks the prompt (OSC 133 A/B), command start (C) and end
# with its status (D), and reports the working directory (OSC 7). It may also be sourced directly from a .bashrc.

# The user's startup files are found at run time.
# shellcheck disable=SC1090,SC1091

if [ -n "${SHARPRAIL_SHELL_INJECT-}" ]; then
  unset SHARPRAIL_SHELL_INJECT
  set +o posix
  if [ -n "${SHARPRAIL_SHELL_BASH_ENV+set}" ]; then
    export ENV="$SHARPRAIL_SHELL_BASH_ENV"
    unset SHARPRAIL_SHELL_BASH_ENV
  else
    unset ENV
  fi
  if [ -n "${SHARPRAIL_SHELL_HISTFILE_UNEXPORT-}" ]; then
    unset SHARPRAIL_SHELL_HISTFILE_UNEXPORT
    export -n HISTFILE
  fi
  # `.` restores the DEBUG trap on return unless functrace is on; keep any trap the user's files set.
  case $- in *T*) _sharprail_functrace=1 ;; *) _sharprail_functrace= ; set -T ;; esac
  if shopt -q login_shell; then
    [ -r /etc/profile ] && . /etc/profile
    for _sharprail_rc in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
      if [ -r "$_sharprail_rc" ]; then . "$_sharprail_rc"; break; fi
    done
    unset _sharprail_rc
  else
    [ -r /etc/bash.bashrc ] && . /etc/bash.bashrc
    [ -r "$HOME/.bashrc" ] && . "$HOME/.bashrc"
  fi
  [ -z "$_sharprail_functrace" ] && set +T
  unset _sharprail_functrace
fi

if [[ $- == *i* && -z ${_sharprail_integrated-} ]]; then
  _sharprail_integrated=1
  # 1 while the prompt is up; the first command after it clears it and marks C, so an empty Enter reports nothing.
  _sharprail_at_prompt=
  _sharprail_cwd=
  _sharprail_trapped=
  _sharprail_install=
  _sharprail_mark_a=$'\[\e]133;A\a\]'
  _sharprail_mark_b=$'\[\e]133;B\a\]'

  _sharprail_report_cwd() {
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
  _sharprail_precmd() {
    local ret=$?
    if [[ $_sharprail_at_prompt == 0 ]]; then printf '\e]133;D;%s\a' "$ret"; fi
    _sharprail_at_prompt=
    if [[ $PWD != "$_sharprail_cwd" ]]; then _sharprail_cwd=$PWD; _sharprail_report_cwd; fi
    return $ret
  }

  # Last in PROMPT_COMMAND, after anything that rebuilds PS1.
  _sharprail_prompt() {
    local ret=$?
    if [[ $PS1 != *$'\e]133;A\a'* ]]; then PS1=$_sharprail_mark_a$PS1$_sharprail_mark_b; fi
    return $ret
  }

  _sharprail_debug() {
    if [[ $_sharprail_at_prompt == 1 && $BASH_COMMAND != _sharprail_precmd* ]]; then
      _sharprail_at_prompt=0
      printf '\e]133;C\a'
    fi
  }

  # The DEBUG trap goes in from the first prompt, at top level: bash restores the DEBUG trap when a function or a
  # sourced file that set it returns, and a function does not see it. A DEBUG trap the user already set (as `trap -p` printed it, its third word the
  # action) keeps running, after ours.
  _sharprail_trap_command() {
    _sharprail_trapped=1
    _sharprail_install=
    eval "set -- $1"
    case $3 in *_sharprail_debug*) return ;; esac
    printf -v _sharprail_install 'trap %q DEBUG' "_sharprail_debug${3:+; $3}"
  }

  # The prompt is up only after the last step, so none of these steps counts as a command.
  PROMPT_COMMAND="_sharprail_precmd${PROMPT_COMMAND:+; $PROMPT_COMMAND}; _sharprail_prompt; [[ -n \$_sharprail_trapped ]] || _sharprail_trap_command \"\$(trap -p DEBUG)\"; eval \"\$_sharprail_install\"; _sharprail_install=; _sharprail_at_prompt=1"
fi
